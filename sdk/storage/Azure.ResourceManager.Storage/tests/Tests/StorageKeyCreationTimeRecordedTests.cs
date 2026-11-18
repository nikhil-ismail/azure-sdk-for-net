// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Core.TestFramework;
using Azure.Core.TestFramework.Models;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Azure.ResourceManager.Storage.Tests
{
    [ClientTestFixture]
    public class StorageKeyCreationTimeRecordedTests : RecordedTestBase<StorageKeyCreationTimeTestEnvironment>
    {
        private readonly string _toolingTagKey;

        public StorageKeyCreationTimeRecordedTests(bool isAsync) : this(isAsync, null)
        {
        }

        internal StorageKeyCreationTimeRecordedTests(bool isAsync, RecordedTestMode? mode, string resourceGroup = null, string accountName = null, string toolingTagKey = null) : base(isAsync, mode)
        {
            if (Mode != RecordedTestMode.Playback)
            {
                if (Mode != RecordedTestMode.Record)
                {
                    throw new InvalidOperationException("Use Record or Playback for this fixture; unrecorded Live mode is not permitted.");
                }
                RequireBootstrappingDisabled(TestContext.Parameters["DisableBootstrapping"] ??
                    Environment.GetEnvironmentVariable("AZURE_DISABLE_BOOTSTRAPPING"));
                TestEnvironment.RequireRecordingApproval();
            }

            _toolingTagKey = toolingTagKey ?? TestEnvironment.ToolingTagKeyForSanitizer;
            RequireToolingTagKey(_toolingTagKey, Mode);

            AdditionalInterceptors = new[] { new ManagementInterceptor(this) };
            IgnoredQueryParameters.Remove("api-version");
            JsonPathSanitizers.Add("$..secretAccessKey");
            JsonPathSanitizers.Add("$..tags.*");
            JsonPathSanitizers.Add("$..systemData.createdBy");
            JsonPathSanitizers.Add("$..systemData.lastModifiedBy");
            JsonPathSanitizers.Add("$..etag");
            ConfigureToolingTagKeySanitizer();
            ConfigureIdentifierSanitizers(resourceGroup ?? TestEnvironment.ResourceGroupForSanitizer,
                accountName ?? TestEnvironment.AccountNameForSanitizer);
            foreach (string header in new[] { "x-ms-request-id", "x-ms-client-request-id", "x-ms-correlation-request-id", "x-ms-routing-request-id", "x-ms-original-request-ids", "ETag" })
            {
                SanitizedHeaders.Add(header);
            }
            foreach (string path in new[] { "$..location", "$..primaryLocation", "$..secondaryLocation" })
            {
                BodyKeySanitizers.Add(new BodyKeySanitizer(path) { Regex = @"(?i)\(stage\)$", Value = string.Empty });
            }
            foreach (string field in new[] { "tenantId", "principalId", "clientId", "objectId", "domainGuid", "federatedIdentityClientId" })
            {
                BodyKeySanitizers.Add(new BodyKeySanitizer("$.." + field) { Value = Guid.Empty.ToString(), Regex = ".+" });
            }
            foreach (string field in new[] { "domainName", "forestName" })
            {
                BodyKeySanitizers.Add(new BodyKeySanitizer("$..activeDirectoryProperties." + field) { Value = "sanitized.example", Regex = ".+" });
            }
            foreach (string field in new[] { "netBiosDomainName", "samAccountName" })
            {
                BodyKeySanitizers.Add(new BodyKeySanitizer("$..activeDirectoryProperties." + field) { Value = "sanitized", Regex = ".+" });
            }
            foreach (string field in new[] { "domainSid", "azureStorageSid" })
            {
                BodyKeySanitizers.Add(new BodyKeySanitizer("$..activeDirectoryProperties." + field)
                {
                    Value = field == "domainSid" ? "S-1-5-21-0-0-0" : "S-1-5-21-0-0-0-0",
                    Regex = ".+"
                });
            }
            foreach (string path in new[] { "$..ipAddressOrRange", "$..ipAddress", "$..ipAddresses[*]" })
            {
                BodyKeySanitizers.Add(new BodyKeySanitizer(path) { Value = "192.0.2.1", Regex = ".+" });
            }
            BodyKeySanitizers.Add(new BodyKeySanitizer("$..ipRules[*].value") { Value = "192.0.2.1", Regex = ".+" });
            BodyKeySanitizers.Add(new BodyKeySanitizer("$..ipv6Rules[*].value") { Value = "2001:db8::1", Regex = ".+" });
            BodyKeySanitizers.Add(new BodyKeySanitizer("$..ipv6AddressOrRange") { Value = "2001:db8::1", Regex = ".+" });
            BodyKeySanitizers.Add(new BodyKeySanitizer("$..customDomain.name") { Value = "sanitized.example", Regex = ".+" });
            foreach (string field in new[] { "keyname", "keyversion" })
            {
                BodyKeySanitizers.Add(new BodyKeySanitizer("$..keyvaultproperties." + field) { Value = "sanitized", Regex = ".+" });
            }
            BodyKeySanitizers.Add(new BodyKeySanitizer("$..keyvaultproperties.keyvaulturi") { Value = "https://sanitized.example/", Regex = ".+" });
            BodyKeySanitizers.Add(new BodyKeySanitizer("$..keyvaultproperties.currentVersionedKeyIdentifier")
            {
                Value = "https://sanitized.example/keys/sanitized/sanitized",
                Regex = ".+"
            });
            JsonPathSanitizers.Add("$..privateLinkServiceConnectionState.description");
            foreach (string endpoints in new[] { "primaryEndpoints", "secondaryEndpoints" })
            {
                foreach (string service in new[] { "blob", "queue", "table", "file", "web", "dfs" })
                {
                    BodyKeySanitizers.Add(new BodyKeySanitizer($"$..{endpoints}..{service}") { Value = "https://sanitized.example/" });
                }
            }
        }

        private void ConfigureToolingTagKeySanitizer()
        {
            const string jsonString = @"""(?:[^""\\]|\\.)*""";
            string key = Regex.Escape(JsonConvert.ToString(_toolingTagKey));
            BodyRegexSanitizers.Add(new BodyRegexSanitizer(
                $@"(?<=""tags""\s*:\s*\{{(?:\s*{jsonString}\s*:\s*(?:{jsonString}|null)\s*,)*\s*){key}(?=\s*:)")
            {
                Value = JsonConvert.ToString(StorageKeyCreationTimeTestEnvironment.SanitizedToolingTagKey)
            });
        }

        private void ConfigureIdentifierSanitizers(string resourceGroup, string accountName)
        {
            const string targetAccount = StorageKeyCreationTimeTestEnvironment.SanitizedAccountName;
            const string targetGroup = StorageKeyCreationTimeTestEnvironment.SanitizedResourceGroup;
            const string accountMarker = "__key_creation_time_account__";
            const string groupMarker = "!key_creation_time_group!";
            foreach (string path in new[] { "$.name", "$.value[*].name" })
            {
                BodyKeySanitizers.Add(new BodyKeySanitizer(path)
                {
                    Regex = $"(?i)^{Regex.Escape(accountName)}$",
                    Value = accountMarker
                });
                BodyKeySanitizers.Add(new BodyKeySanitizer(path) { Regex = $"^(?!{accountMarker}$).+", Value = "sanitized" });
            }
            BodyKeySanitizers.Add(new BodyKeySanitizer("$..privateEndpointConnections[*].name") { Value = "sanitized", Regex = ".+" });

            // Apply identical path replacements to request URIs and JSON (including nextLink and identity dictionary keys).
            foreach (var (pattern, replacement) in new[]
            {
                (@"(?i)(?<=/subscriptions/)[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}", Guid.Empty.ToString()),
                ($@"(?i)(?<=/resourceGroups/){Regex.Escape(resourceGroup)}(?=[/""?\\]|$)", groupMarker),
                ($@"(?i)(?<=/storageAccounts/){Regex.Escape(accountName)}(?=[/""?\\]|$)", accountMarker),
                ($@"(?i)(?<=/resourceGroups/)(?!{groupMarker}(?=[/""?\\]|$))[^/""?\\]+", "sanitized"),
                ($@"(?i)(?<=/providers/[^/""?\\]+/(?:[^/""?\\]+/[^/""?\\]+/)*[^/""?\\]+/)(?!{accountMarker}(?=[/""?\\]|$))[^/""?\\]+", "sanitized"),
                (groupMarker, targetGroup),
                (accountMarker, targetAccount)
            })
            {
                UriRegexSanitizers.Add(new UriRegexSanitizer(pattern) { Value = replacement });
                BodyRegexSanitizers.Add(new BodyRegexSanitizer(pattern) { Value = replacement });
            }
            BodyRegexSanitizers.Add(new BodyRegexSanitizer(
                @"(?i)(?<=https?://)(?!(?:eastus2euap\.management\.azure\.com|management\.azure\.com)(?=/|""|$))[^/""\\\s]+")
            {
                Value = "sanitized.example"
            });
        }

        internal static void RequireToolingTagKey(string key, RecordedTestMode mode)
        {
            if (string.IsNullOrWhiteSpace(key) ||
                mode == RecordedTestMode.Record && key.Equals(StorageKeyCreationTimeTestEnvironment.SanitizedToolingTagKey, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Supply the real exact tooling tag key privately through STORAGE_KEY_CREATION_TIME_TOOLING_TAG_KEY before Record. The canonical replacement is reserved for Playback. Do not put the private value in tracked configuration or logs.");
            }
        }

        internal static void RequireBootstrappingDisabled(string setting)
        {
            if (!bool.TryParse(setting, out bool disabled) || !disabled)
            {
                throw new InvalidOperationException("Set AZURE_DISABLE_BOOTSTRAPPING=true before constructing this fixture. Resource bootstrapping and expiration updates are prohibited.");
            }
        }

        [RecordedTest]
        public async Task GetAndListsExposeKeyCreationTimestamps()
        {
            ResourceIdentifier accountId = StorageAccountResource.CreateResourceIdentifier(
                TestEnvironment.TargetSubscriptionId, TestEnvironment.TargetResourceGroup, TestEnvironment.TargetAccountName);
            Uri endpoint = new Uri(TestEnvironment.TargetEndpoint);
            string apiVersion = TestEnvironment.TargetApiVersion;
            ArmClientOptions options = CreateReadOnlyOptions(endpoint, TestEnvironment.TargetAudience, accountId, apiVersion);
            ArmClient client = InstrumentClient(new ArmClient(
                TestEnvironment.Credential, accountId.SubscriptionId, InstrumentClientOptions(options)));

            int[] pages = await StorageKeyCreationTimeValidation.VerifyReadsAsync(client, accountId, _toolingTagKey);
            TestContext.WriteLine($"Validated api-version={apiVersion}: GET, RG LIST ({pages[0]} page(s)), and subscription LIST ({pages[1]} page(s)).");
            if (pages[0] == 1 || pages[1] == 1)
            {
                TestContext.WriteLine("Live pagination was not exercised for each single-page scope; forced pagination is covered by offline tests.");
            }
        }

        internal static ArmClientOptions CreateReadOnlyOptions(Uri endpoint, string audience, ResourceIdentifier accountId, string apiVersion = null)
        {
            if (!Uri.TryCreate(audience, UriKind.Absolute, out Uri audienceUri) || audienceUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("An explicit approved HTTPS ARM token audience is required.");
            }

            var options = new ArmClientOptions
            {
                Environment = new ArmEnvironment(endpoint, audience.TrimEnd('/'))
            };
            string selectedApiVersion = StorageKeyCreationTimeReadOnlyPolicy.ResolveApiVersion(apiVersion);
            options.SetApiVersion(StorageAccountResource.ResourceType, selectedApiVersion);
            var guard = new StorageKeyCreationTimeReadOnlyPolicy(endpoint, accountId, selectedApiVersion);
            options.AddPolicy(guard, HttpPipelinePosition.PerCall);
            options.AddPolicy(guard, HttpPipelinePosition.PerRetry);
            return options;
        }
    }
}
