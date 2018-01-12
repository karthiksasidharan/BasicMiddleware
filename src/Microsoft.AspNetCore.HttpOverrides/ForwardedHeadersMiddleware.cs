// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Microsoft.AspNetCore.HttpOverrides
{
    public class ForwardedHeadersMiddleware
    {
        private const string XForwardedForHeaderName = "X-Forwarded-For";
        private const string XForwardedHostHeaderName = "X-Forwarded-Host";
        private const string XForwardedProtoHeaderName = "X-Forwarded-Proto";
        private const string XOriginalForName = "X-Original-For";
        private const string XOriginalHostName = "X-Original-Host";
        private const string XOriginalProtoName = "X-Original-Proto";

        private static readonly bool[] HostCharValidity = new bool[127];
        private static readonly bool[] SchemeCharValidity = new bool[123];

        private readonly ForwardedHeadersOptions _options;
        private readonly RequestDelegate _next;
        private readonly ILogger _logger;

        static ForwardedHeadersMiddleware()
        {
            // RFC 3986 scheme = ALPHA * (ALPHA / DIGIT / "+" / "-" / ".")
            SchemeCharValidity['+'] = true;
            SchemeCharValidity['-'] = true;
            SchemeCharValidity['.'] = true;

            // Host Matches Http.Sys and Kestrel
            // Host Matches RFC 3986 except "*" / "+" / "," / ";" / "=" and "%" HEXDIG HEXDIG which are not allowed by Http.Sys
            HostCharValidity['!'] = true;
            HostCharValidity['$'] = true;
            HostCharValidity['&'] = true;
            HostCharValidity['\''] = true;
            HostCharValidity['('] = true;
            HostCharValidity[')'] = true;
            HostCharValidity['-'] = true;
            HostCharValidity['.'] = true;
            HostCharValidity['_'] = true;
            HostCharValidity['~'] = true;
            for (var ch = '0'; ch <= '9'; ch++)
            {
                SchemeCharValidity[ch] = true;
                HostCharValidity[ch] = true;
            }
            for (var ch = 'A'; ch <= 'Z'; ch++)
            {
                SchemeCharValidity[ch] = true;
                HostCharValidity[ch] = true;
            }
            for (var ch = 'a'; ch <= 'z'; ch++)
            {
                SchemeCharValidity[ch] = true;
                HostCharValidity[ch] = true;
            }
        }

        public ForwardedHeadersMiddleware(RequestDelegate next, ILoggerFactory loggerFactory, IOptions<ForwardedHeadersOptions> options)
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

            _options = options.Value;
            _logger = loggerFactory.CreateLogger<ForwardedHeadersMiddleware>();
            _next = next;
        }

        public Task Invoke(HttpContext context)
        {
            ApplyForwarders(context);
            return _next(context);
        }

        public void ApplyForwarders(HttpContext context)
        {
            // Gather expected headers. Enabled headers must have the same number of entries.
            string[] forwardedFor = null, forwardedProto = null, forwardedHost = null;
            bool checkFor = false, checkProto = false, checkHost = false;
            int entryCount = 0;

            if ((_options.ForwardedHeaders & ForwardedHeaders.XForwardedFor) == ForwardedHeaders.XForwardedFor)
            {
                checkFor = true;
                forwardedFor = context.Request.Headers.GetCommaSeparatedValues(XForwardedForHeaderName);
                entryCount = Math.Max(forwardedFor.Length, entryCount);
            }

            if ((_options.ForwardedHeaders & ForwardedHeaders.XForwardedProto) == ForwardedHeaders.XForwardedProto)
            {
                checkProto = true;
                forwardedProto = context.Request.Headers.GetCommaSeparatedValues(XForwardedProtoHeaderName);
                if (_options.RequireHeaderSymmetry && checkFor && forwardedFor.Length != forwardedProto.Length)
                {
                    _logger.LogDebug(1, "Parameter count mismatch between X-Forwarded-For and X-Forwarded-Proto.");
                    return;
                }
                entryCount = Math.Max(forwardedProto.Length, entryCount);
            }

            if ((_options.ForwardedHeaders & ForwardedHeaders.XForwardedHost) == ForwardedHeaders.XForwardedHost)
            {
                checkHost = true;
                forwardedHost = context.Request.Headers.GetCommaSeparatedValues(XForwardedHostHeaderName);
                if (_options.RequireHeaderSymmetry
                    && ((checkFor && forwardedFor.Length != forwardedHost.Length)
                        || (checkProto && forwardedProto.Length != forwardedHost.Length)))
                {
                    _logger.LogDebug(1, "Parameter count mismatch between X-Forwarded-Host and X-Forwarded-For or X-Forwarded-Proto.");
                    return;
                }
                entryCount =  Math.Max(forwardedHost.Length, entryCount);
            }

            // Apply ForwardLimit, if any
            if (_options.ForwardLimit.HasValue && entryCount > _options.ForwardLimit)
            {
                entryCount = _options.ForwardLimit.Value;
            }

            // Group the data together.
            var sets = new SetOfForwarders[entryCount];
            for (int i = 0; i < sets.Length; i++)
            {
                // They get processed in reverse order, right to left.
                var set = new SetOfForwarders();
                if (checkFor && i < forwardedFor.Length)
                {
                    set.IpAndPortText = forwardedFor[forwardedFor.Length - i - 1];
                }
                if (checkProto && i < forwardedProto.Length)
                {
                    set.Scheme = forwardedProto[forwardedProto.Length - i - 1];
                }
                if (checkHost && i < forwardedHost.Length)
                {
                    set.Host = forwardedHost[forwardedHost.Length - i - 1];
                }
                sets[i] = set;
            }

            // Gather initial values
            var connection = context.Connection;
            var request = context.Request;
            var currentValues = new SetOfForwarders()
            {
                RemoteIpAndPort = connection.RemoteIpAddress != null ? new IPEndPoint(connection.RemoteIpAddress, connection.RemotePort) : null,
                // Host and Scheme initial values are never inspected, no need to set them here.
            };

            var checkKnownIps = _options.KnownNetworks.Count > 0 || _options.KnownProxies.Count > 0;
            bool applyChanges = false;
            int entriesConsumed = 0;

            for ( ; entriesConsumed < sets.Length; entriesConsumed++)
            {
                var set = sets[entriesConsumed];
                if (checkFor)
                {
                    // For the first instance, allow remoteIp to be null for servers that don't support it natively.
                    if (currentValues.RemoteIpAndPort != null && checkKnownIps && !CheckKnownAddress(currentValues.RemoteIpAndPort.Address))
                    {
                        // Stop at the first unknown remote IP, but still apply changes processed so far.
                        _logger.LogDebug(1, $"Unknown proxy: {currentValues.RemoteIpAndPort}");
                        break;
                    }

                    IPEndPoint parsedEndPoint;
                    if (IPEndPointParser.TryParse(set.IpAndPortText, out parsedEndPoint))
                    {
                        applyChanges = true;
                        set.RemoteIpAndPort = parsedEndPoint;
                        currentValues.IpAndPortText = set.IpAndPortText;
                        currentValues.RemoteIpAndPort = set.RemoteIpAndPort;
                    }
                    else if (_options.RequireHeaderSymmetry)
                    {
                        _logger.LogDebug(2, $"Failed to parse forwarded IPAddress: {currentValues.IpAndPortText}");
                        return;
                    }
                }

                if (checkProto)
                {
                    if (!string.IsNullOrEmpty(set.Scheme) && (_options.UseRelaxedHeaderValidation || TryValidateScheme(set.Scheme)))
                    {
                        applyChanges = true;
                        currentValues.Scheme = set.Scheme;
                    }
                    else if (_options.RequireHeaderSymmetry)
                    {
                        _logger.LogDebug(3, $"Failed to parse forwarded scheme: {set.Scheme}");
                        return;
                    }
                }

                if (checkHost)
                {
                    if (!string.IsNullOrEmpty(set.Host) && (_options.UseRelaxedHeaderValidation || TryValidateHost(set.Host)))
                    {
                        applyChanges = true;
                        currentValues.Host = set.Host;
                    }
                    else if (_options.RequireHeaderSymmetry)
                    {
                        _logger.LogDebug(4, $"Failed to parse forwarded host: {set.Host}");
                        return;
                    }
                }
            }

            if (applyChanges)
            {
                if (checkFor && currentValues.RemoteIpAndPort != null)
                {
                    if (connection.RemoteIpAddress != null)
                    {
                        // Save the original
                        request.Headers[XOriginalForName] = new IPEndPoint(connection.RemoteIpAddress, connection.RemotePort).ToString();
                    }
                    if (forwardedFor.Length > entriesConsumed)
                    {
                        // Truncate the consumed header values
                        request.Headers[XForwardedForHeaderName] = forwardedFor.Take(forwardedFor.Length - entriesConsumed).ToArray();
                    }
                    else
                    {
                        // All values were consumed
                        request.Headers.Remove(XForwardedForHeaderName);
                    }
                    connection.RemoteIpAddress = currentValues.RemoteIpAndPort.Address;
                    connection.RemotePort = currentValues.RemoteIpAndPort.Port;
                }

                if (checkProto && currentValues.Scheme != null)
                {
                    // Save the original
                    request.Headers[XOriginalProtoName] = request.Scheme;
                    if (forwardedProto.Length > entriesConsumed)
                    {
                        // Truncate the consumed header values
                        request.Headers[XForwardedProtoHeaderName] = forwardedProto.Take(forwardedProto.Length - entriesConsumed).ToArray();
                    }
                    else
                    {
                        // All values were consumed
                        request.Headers.Remove(XForwardedProtoHeaderName);
                    }
                    request.Scheme = currentValues.Scheme;
                }

                if (checkHost && currentValues.Host != null)
                {
                    // Save the original
                    request.Headers[XOriginalHostName] = request.Host.ToString();
                    if (forwardedHost.Length > entriesConsumed)
                    {
                        // Truncate the consumed header values
                        request.Headers[XForwardedHostHeaderName] = forwardedHost.Take(forwardedHost.Length - entriesConsumed).ToArray();
                    }
                    else
                    {
                        // All values were consumed
                        request.Headers.Remove(XForwardedHostHeaderName);
                    }
                    request.Host = HostString.FromUriComponent(currentValues.Host);
                }
            }
        }

        private bool CheckKnownAddress(IPAddress address)
        {
            if (_options.KnownProxies.Contains(address))
            {
                return true;
            }
            foreach (var network in _options.KnownNetworks)
            {
                if (network.Contains(address))
                {
                    return true;
                }
            }
            return false;
        }

        private struct SetOfForwarders
        {
            public string IpAndPortText;
            public IPEndPoint RemoteIpAndPort;
            public string Host;
            public string Scheme;
        }

        // Empty was checked for by the caller
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryValidateScheme(string scheme)
        {
            for (var i = 0; i < scheme.Length; i++)
            {
                if (!IsValidSchemeChar(scheme[i]))
                {
                    return false;
                }
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsValidSchemeChar(char ch)
        {
            return ch < SchemeCharValidity.Length && SchemeCharValidity[ch];
        }

        // Empty was checked for by the caller
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryValidateHost(string host)
        {
            if (host[0] == '[')
            {
                return TryValidateIPv6Host(host);
            }

            if (host[0] == ':')
            {
                // Only a port
                return false;
            }

            var i = 0;
            for (; i < host.Length; i++)
            {
                if (!IsValidHostChar(host[i]))
                {
                    break;
                }
            }
            return TryValidateHostPort(host, i);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsValidHostChar(char ch)
        {
            return ch < HostCharValidity.Length && HostCharValidity[ch];
        }

        // The lead '[' was already checked
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryValidateIPv6Host(string hostText)
        {
            for (var i = 1; i < hostText.Length; i++)
            {
                var ch = hostText[i];
                if (ch == ']')
                {
                    // [::1] is the shortest valid IPv6 host
                    if (i < 4)
                    {
                        return false;
                    }
                    return TryValidateHostPort(hostText, i + 1);
                }

                if (!IsHex(ch) && ch != ':' && ch != '.')
                {
                    return false;
                }
            }

            // Must contain a ']'
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryValidateHostPort(string hostText, int offset)
        {
            if (offset == hostText.Length)
            {
                // No port
                return true;
            }

            if (hostText[offset] != ':' || hostText.Length == offset + 1)
            {
                // Must have at least one number after the colon if present.
                return false;
            }

            for (var i = offset + 1; i < hostText.Length; i++)
            {
                if (!IsNumeric(hostText[i]))
                {
                    return false;
                }
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsNumeric(char ch)
        {
            return '0' <= ch && ch <= '9';
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsHex(char ch)
        {
            return IsNumeric(ch)
                || ('a' <= ch && ch <= 'f')
                || ('A' <= ch && ch <= 'F');
        }
    }
}
