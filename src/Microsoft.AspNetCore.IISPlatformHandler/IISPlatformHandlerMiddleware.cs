// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Globalization;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Features.Authentication;
using Microsoft.AspNetCore.Http.Features.Authentication.Internal;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Microsoft.AspNetCore.IISPlatformHandler
{
    public class IISPlatformHandlerMiddleware
    {
        private const string XIISWindowsAuthToken = "X-IIS-WindowsAuthToken"; // TODO: Legacy, remove before RTW
        private const string MSPlatformHandlerWinAuthToken = "MS-PLATFORM-HANDLER-WINAUTHTOKEN";
        private const string MSPlatformHandlerClientCert = "MS-PLATFORM-HANDLER-CLIENTCERT";
        private const string HttpPlatformToken = "HTTP_PLATFORM_TOKEN";
        private const string MSPlatformHandlerToken = "MS-PLATFORM-HANDLER-TOKEN";

        private readonly RequestDelegate _next;
        private readonly IISPlatformHandlerOptions _options;
        private readonly ILogger _logger;
        private readonly string _platformToken;

        public IISPlatformHandlerMiddleware(RequestDelegate next, ILoggerFactory loggerFactory, IOptions<IISPlatformHandlerOptions> options)
        {
            if (next == null)
            {
                throw new ArgumentNullException(nameof(next));
            }
            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _next = next;
            _options = options.Value;
            _logger = loggerFactory.CreateLogger<IISPlatformHandlerMiddleware>();

            _platformToken = Environment.GetEnvironmentVariable(HttpPlatformToken);
            if (string.IsNullOrEmpty(_platformToken))
            {
                _logger.LogInformation($"{HttpPlatformToken} not detected, {nameof(IISPlatformHandlerMiddleware)} will be skipped.");
            }
        }

        public async Task Invoke(HttpContext httpContext)
        {
            if (string.IsNullOrEmpty(_platformToken)
                || !string.Equals(_platformToken, httpContext.Request.Headers[MSPlatformHandlerToken], StringComparison.Ordinal))
            {
                _logger.LogTrace($"{HttpPlatformToken} not detected, skipping {nameof(IISPlatformHandlerMiddleware)}.");
                await _next(httpContext);
            }

            if (_options.ForwardClientCertificate)
            {
                var header = httpContext.Request.Headers[MSPlatformHandlerClientCert];
                if (!StringValues.IsNullOrEmpty(header))
                {
                    httpContext.Features.Set<ITlsConnectionFeature>(new ForwardedTlsConnectionFeature(_logger, header));
                }
            }

            if (_options.ForwardWindowsAuthentication)
            {
                var winPrincipal = UpdateUser(httpContext);
                var handler = new AuthenticationHandler(httpContext, _options, winPrincipal);
                AttachAuthenticationHandler(handler);
                try
                {
                    await _next(httpContext);
                }
                finally
                {
                   DetachAuthenticationhandler(handler);
                }
            }
            else
            {
                await _next(httpContext);
            }
        }

        private WindowsPrincipal UpdateUser(HttpContext httpContext)
        {
            var tokenHeader = httpContext.Request.Headers[MSPlatformHandlerWinAuthToken];

            if (StringValues.IsNullOrEmpty(tokenHeader))
            {
                // TODO: Legacy, remove before RTW
                tokenHeader = httpContext.Request.Headers[XIISWindowsAuthToken];
            }

            int hexHandle;
            WindowsPrincipal winPrincipal = null;
            if (!StringValues.IsNullOrEmpty(tokenHeader)
                && int.TryParse(tokenHeader, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hexHandle))
            {
                // Always create the identity if the handle exists, we need to dispose it so it does not leak.
                var handle = new IntPtr(hexHandle);
                var winIdentity = new WindowsIdentity(handle);

                // WindowsIdentity just duplicated the handle so we need to close the original.
                NativeMethods.CloseHandle(handle);

                httpContext.Response.RegisterForDispose(winIdentity);
                winPrincipal = new WindowsPrincipal(winIdentity);

                if (_options.AutomaticAuthentication)
                {
                    // Don't get it from httpContext.User, that always returns a non-null anonymous user by default.
                    var existingPrincipal = httpContext.Features.Get<IHttpAuthenticationFeature>()?.User;
                    if (existingPrincipal != null)
                    {
                        httpContext.User = SecurityHelper.MergeUserPrincipal(existingPrincipal, winPrincipal);
                    }
                    else
                    {
                        httpContext.User = winPrincipal;
                    }
                }
            }

            return winPrincipal;
        }

        private void AttachAuthenticationHandler(AuthenticationHandler handler)
        {
            var auth = handler.HttpContext.Features.Get<IHttpAuthenticationFeature>();
            if (auth == null)
            {
                auth = new HttpAuthenticationFeature();
                handler.HttpContext.Features.Set(auth);
            }
            handler.PriorHandler = auth.Handler;
            auth.Handler = handler;
        }

        private void DetachAuthenticationhandler(AuthenticationHandler handler)
        {
            var auth = handler.HttpContext.Features.Get<IHttpAuthenticationFeature>();
            if (auth != null)
            {
                auth.Handler = handler.PriorHandler;
            }
        }
    }
}
