// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Linq;
using Azure.Core;
using Azure.Core.Pipeline;

namespace Azure.ResourceManager.Storage.Tests
{
    internal sealed class StorageKeyCreationTimeReadOnlyPolicy : HttpPipelineSynchronousPolicy
    {
        internal const string DefaultApiVersion = "2026-12-01";
        internal const string ApprovedCanaryApiVersion = "2026-09-01";
        private readonly Uri _endpoint;
        private readonly string[] _paths;
        private readonly string _apiVersion;

        internal StorageKeyCreationTimeReadOnlyPolicy(Uri endpoint, ResourceIdentifier accountId, string apiVersion = null)
        {
            _apiVersion = ResolveApiVersion(apiVersion);
            if (endpoint.Scheme != Uri.UriSchemeHttps || endpoint.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.UserInfo) ||
                !string.IsNullOrEmpty(endpoint.Fragment))
            {
                throw new ArgumentException("An explicit HTTPS ARM endpoint without a path, credentials, query, or fragment is required.", nameof(endpoint));
            }

            _endpoint = endpoint;
            _paths = new[]
            {
                accountId.ToString(),
                $"/subscriptions/{accountId.SubscriptionId}/resourceGroups/{accountId.ResourceGroupName}/providers/Microsoft.Storage/storageAccounts",
                $"/subscriptions/{accountId.SubscriptionId}/providers/Microsoft.Storage/storageAccounts"
            };
        }

        internal static string ResolveApiVersion(string apiVersion)
        {
            if (apiVersion == null)
            {
                return DefaultApiVersion;
            }
            if (apiVersion != DefaultApiVersion && apiVersion != ApprovedCanaryApiVersion)
            {
                throw new ArgumentException("Only the December default or explicitly approved September Canary request version is supported.", nameof(apiVersion));
            }
            return apiVersion;
        }

        public override void OnSendingRequest(HttpMessage message)
        {
            Uri uri = message.Request.Uri.ToUri();
            string[] versions = uri.Query.TrimStart('?').Split('&')
                .Select(pair => pair.Split(new[] { '=' }, 2))
                .Where(pair => Uri.UnescapeDataString(pair[0]).Equals("api-version", StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty)
                .ToArray();

            if (message.Request.Method != RequestMethod.Get ||
                !uri.GetLeftPart(UriPartial.Authority).Equals(_endpoint.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
                !_paths.Contains(uri.AbsolutePath, StringComparer.OrdinalIgnoreCase) ||
                versions.Length != 1 || versions[0] != _apiVersion)
            {
                throw new InvalidOperationException($"Only target-account GET and its resource-group/subscription LIST requests with api-version={_apiVersion} are permitted.");
            }
        }
    }
}
