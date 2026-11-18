// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Core.TestFramework;
using Azure.ResourceManager.Storage.Models;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NUnit.Framework.Internal;

namespace Azure.ResourceManager.Storage.Tests
{
    [ClientTestFixture]
    public class StorageKeyCreationTimeUnitTests : ClientTestBase
    {
        private const string SubscriptionId = "00000000-0000-0000-0000-000000000000";
        private static readonly Uri Endpoint = new Uri("https://eastus2euap.management.azure.com/");
        private static readonly ResourceIdentifier AccountId = StorageAccountResource.CreateResourceIdentifier(SubscriptionId, "test-rg", "targetaccount");
        private const string AllTimestamps = """
            {"key1":"2026-10-11T01:02:03.1234567Z","key2":"2026-10-12T01:02:03.1234567Z",
             "kerb1":"2026-10-13T01:02:03.1234567Z","kerb2":"2026-10-14T01:02:03.1234567Z",
             "keytab1":"2026-10-15T01:02:03.1234567Z","keytab2":"2026-10-16T01:02:03.1234567Z"}
            """;

        public StorageKeyCreationTimeUnitTests(bool isAsync) : base(isAsync)
        {
            AdditionalInterceptors = new[] { new ManagementInterceptor(this) };
        }

        [TestCase(AllTimestamps, null)]
        [TestCase(AllTimestamps, "2026-12-01")]
        [TestCase(AllTimestamps, "2026-09-01")]
        [TestCase("""{"key1":"2026-10-11T01:02:03.1234567Z","kerb1":"2026-10-13T01:02:03.1234567Z"}""", null)]
        [TestCase("""{"key1":"2026-10-11T01:02:03.1234567Z","kerb1":"2026-10-13T01:02:03.1234567Z"}""", "2026-09-01")]
        public async Task GetAndListsValidateTargetOnSecondPage(string timestamps, string apiVersion)
        {
            string expectedVersion = apiVersion ?? "2026-12-01";
            string nextLinkVersion = expectedVersion == "2026-09-01" ? "2026-12-01" : "2026-09-01";
            string account = AccountJson(AccountId, timestamps);
            string unrelated = AccountJson(StorageAccountResource.CreateResourceIdentifier(SubscriptionId, "other-rg", "otheraccount"), null);
            string rgPath = $"/subscriptions/{SubscriptionId}/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts";
            string subPath = $"/subscriptions/{SubscriptionId}/providers/Microsoft.Storage/storageAccounts";
            MockResponse[] responses =
            {
                Response(account),
                Response(ListJson(unrelated, Endpoint + rgPath.TrimStart('/') + $"?api-version={nextLinkVersion}&$skiptoken=rg-page-2")),
                Response(ListJson(account)),
                Response(ListJson(unrelated, subPath + $"?api-version={nextLinkVersion}&$skiptoken=sub-page-2")),
                Response(ListJson(account))
            };
            var transport = new MethodCheckingTransport(IsAsync, responses);
            ArmClient client = CreateClient(transport, apiVersion);

            int[] pages = await StorageKeyCreationTimeValidation.VerifyReadsAsync(client, AccountId);

            CollectionAssert.AreEqual(new[] { 2, 2 }, pages);
            Assert.AreEqual(5, transport.Requests.Count, "Only GET and the two fully enumerated LISTs may execute.");
            Assert.IsTrue(transport.Requests[2].Uri.ToUri().Query.Contains("$skiptoken=rg-page-2"));
            Assert.IsTrue(transport.Requests[4].Uri.ToUri().Query.Contains("$skiptoken=sub-page-2"));
            Assert.IsTrue(transport.Requests.All(request => request.Method == RequestMethod.Get));
            Assert.IsTrue(transport.Requests.All(request => request.Uri.ToUri().Query.Contains("api-version=" + expectedVersion)),
                "GET, both LISTs, and absolute/relative continuation URLs must use exactly the selected version, overriding conflicting nextLink versions.");
        }

        [TestCase("missing")]
        [TestCase("all-null")]
        [TestCase("changed")]
        public void ListWithoutMatchingPositiveTargetFails(string scenario)
        {
            string account = AccountJson(AccountId, AllTimestamps);
            string listAccount = scenario == "missing"
                ? AccountJson(StorageAccountResource.CreateResourceIdentifier(SubscriptionId, "test-rg", "otheraccount"), null)
                : AccountJson(AccountId, scenario == "all-null" ? "{}" : AllTimestamps.Replace("2026-10-13", "2026-10-14"));
            var transport = new MethodCheckingTransport(IsAsync, Response(account), Response(ListJson(listAccount)));
            ArmClient client = CreateClient(transport);
            // Expected assertions inside async code must not mark the outer test result as failed.
            using (new TestExecutionContext.IsolatedContext())
            {
                Assert.ThrowsAsync<AssertionException>(() => StorageKeyCreationTimeValidation.VerifyReadsAsync(client, AccountId));
            }
            Assert.AreEqual(2, transport.Requests.Count);
        }

        [TestCase("{}")]
        [TestCase("""{"kerb1":null,"kerb2":null,"keytab1":null,"keytab2":null}""")]
        [TestCase("""{"key1":"2026-10-11T01:02:03.1234567Z","key2":"2026-10-12T01:02:03.1234567Z"}""")]
        public void OptionalTimestampsAreNotPositiveEvidence(string timestamps)
        {
            using JsonDocument document = JsonDocument.Parse(AccountJson(AccountId, timestamps));
            StorageAccountData account = ModelReaderWriter.Read<StorageAccountData>(BinaryData.FromString(document.RootElement.GetRawText()));
            StorageKeyCreationTimeValidation.AssertTimestamps(document.RootElement, account.KeyCreationTime, requireFeature: false);
            Assert.Throws<AssertionException>(() =>
                StorageKeyCreationTimeValidation.AssertTimestamps(document.RootElement, account.KeyCreationTime, requireFeature: true));
        }

        [TestCase(null)]
        [TestCase("null")]
        public void OptionalTimestampObjectCanBeAbsentOrNull(string timestamps)
        {
            using JsonDocument document = JsonDocument.Parse(AccountJson(AccountId, timestamps));
            StorageAccountData account = ModelReaderWriter.Read<StorageAccountData>(BinaryData.FromString(document.RootElement.GetRawText()));
            Assert.IsNull(account.KeyCreationTime);
            StorageKeyCreationTimeValidation.AssertTimestamps(document.RootElement, account.KeyCreationTime, requireFeature: false);
            Assert.Throws<AssertionException>(() =>
                StorageKeyCreationTimeValidation.AssertTimestamps(document.RootElement, account.KeyCreationTime, requireFeature: true));
        }

        [Test]
        public void TypedTimestampMismatchFails()
        {
            using JsonDocument document = JsonDocument.Parse(AccountJson(AccountId, AllTimestamps));
            var wrong = ArmStorageModelFactory.StorageAccountKeyCreationTime(kerb1: DateTimeOffset.MinValue);
            Assert.Throws<AssertionException>(() =>
                StorageKeyCreationTimeValidation.AssertTimestamps(document.RootElement, wrong, requireFeature: true));
        }

        [Test]
        public void SentinelTimestampDoesNotCountAsPositiveEvidence()
        {
            const string timestamps = """{"kerb1":"0001-01-01T00:00:00Z"}""";
            using JsonDocument document = JsonDocument.Parse(AccountJson(AccountId, timestamps));
            var model = ModelReaderWriter.Read<StorageAccountKeyCreationTime>(BinaryData.FromString(timestamps));
            Assert.Throws<AssertionException>(() =>
                StorageKeyCreationTimeValidation.AssertTimestamps(document.RootElement, model, requireFeature: true));
        }

        [Test]
        public void PlaybackRequiresApiVersionMatchingAndAllowsRecording()
        {
            var fixture = new StorageKeyCreationTimeRecordedTests(IsAsync, RecordedTestMode.Playback);
            Assert.IsFalse(fixture.IgnoredQueryParameters.Contains("api-version"));
            Assert.IsEmpty(typeof(StorageKeyCreationTimeRecordedTests).GetCustomAttributes(typeof(PlaybackOnlyAttribute), inherit: true));
        }

        [Test]
        public void EndpointSanitizersPreserveNestedModelShapesAndTimestamps()
        {
            var fixture = new StorageKeyCreationTimeRecordedTests(IsAsync, RecordedTestMode.Playback);
            JObject account = JObject.Parse(AccountJson(AccountId, AllTimestamps));
            account["properties"]["primaryEndpoints"] = JObject.Parse("""
                {"blob":"https://target.blob.example/",
                 "microsoftEndpoints":{"blob":"https://target.microsoft.blob.example/"},
                 "internetEndpoints":{"blob":"https://target.internet.blob.example/"},
                 "ipv6Endpoints":{"blob":"https://target.ipv6.blob.example/",
                     "microsoftEndpoints":{"blob":"https://target.ipv6.microsoft.blob.example/"}}}
                """);
            account["properties"]["secondaryEndpoints"] = JObject.Parse("""{"queue":"https://secondary.queue.example/"}""");
            int replaced = 0;
            foreach (var sanitizer in fixture.BodyKeySanitizers.Where(sanitizer =>
                sanitizer.JsonPath.StartsWith("$..primaryEndpoints", StringComparison.Ordinal) ||
                sanitizer.JsonPath.StartsWith("$..secondaryEndpoints", StringComparison.Ordinal)))
            {
                foreach (JToken token in account.SelectTokens(sanitizer.JsonPath).ToArray())
                {
                    Assert.AreEqual(JTokenType.String, token.Type, "Endpoint sanitizers must not replace nested objects.");
                    token.Replace(sanitizer.Value);
                    replaced++;
                }
            }
            Assert.AreEqual(6, replaced);
            var model = ModelReaderWriter.Read<StorageAccountData>(BinaryData.FromString(account.ToString()));
            Assert.AreEqual("sanitized.example", model.PrimaryEndpoints.MicrosoftEndpoints.BlobUri.Host);
            Assert.AreEqual("sanitized.example", model.PrimaryEndpoints.InternetEndpoints.BlobUri.Host);
            Assert.AreEqual("sanitized.example", model.PrimaryEndpoints.IPv6Endpoints.MicrosoftEndpoints.BlobUri.Host);
            Assert.AreEqual("sanitized.example", model.SecondaryEndpoints.QueueUri.Host);
            using JsonDocument document = JsonDocument.Parse(account.ToString());
            StorageKeyCreationTimeValidation.AssertTimestamps(document.RootElement, model.KeyCreationTime, requireFeature: true);
        }

        [Test]
        public void OriginalRequestIdsAreRedactedWithoutChangingProtocolHeaders()
        {
            var fixture = new StorageKeyCreationTimeRecordedTests(IsAsync, RecordedTestMode.Playback);
            var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-MS-Original-Request-Ids"] = new[]
                {
                    "11111111-1111-1111-1111-111111111111",
                    "22222222-2222-2222-2222-222222222222"
                },
                ["Content-Type"] = new[] { "application/json" },
                ["Content-Length"] = new[] { "123" },
                ["Retry-After"] = new[] { "5" },
                ["WWW-Authenticate"] = new[] { "Bearer authorization_uri=\"https://login.example/organizations\"" },
                ["Location"] = new[] { new Uri(Endpoint, AccountId + "?api-version=2026-09-01").ToString() },
                ["traceparent"] = new[] { "00-11111111111111111111111111111111-2222222222222222-01" }
            };
            Dictionary<string, string[]> original = headers.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
            Assert.IsTrue(fixture.SanitizedHeaders.Contains("x-ms-original-request-ids", StringComparer.OrdinalIgnoreCase));
            foreach (string name in fixture.SanitizedHeaders)
            {
                if (headers.TryGetValue(name, out string[] values))
                {
                    headers[name] = values.Select(_ => RecordedTestBase.SanitizeValue).ToArray();
                }
            }
            CollectionAssert.AreEqual(new[] { "Sanitized", "Sanitized" }, headers["X-MS-Original-Request-Ids"]);
            foreach (var header in original.Where(pair => !pair.Key.Equals("X-MS-Original-Request-Ids", StringComparison.OrdinalIgnoreCase)))
            {
                CollectionAssert.AreEqual(header.Value, headers[header.Key], "Behavioral headers and unrelated diagnostics must not be blanket-redacted.");
            }
        }

        [TestCase("example-region(stage)", "example-region")]
        [TestCase("example-region(STAGE)", "example-region")]
        [TestCase("eastus", "eastus")]
        [TestCase("eastus2euap", "eastus2euap")]
        public void OnlyStagingQualifierIsRemovedFromRegionFields(string location, string expected)
        {
            var fixture = new StorageKeyCreationTimeRecordedTests(IsAsync, RecordedTestMode.Playback);
            JObject account = ParseSanitizerJson(AccountJson(AccountId, AllTimestamps));
            account["location"] = location;
            account["properties"]["primaryLocation"] = location;
            account["properties"]["secondaryLocation"] = location;
            JObject actual = ParseSanitizerJson(ApplyBodySanitizers(fixture, account.ToString(Newtonsoft.Json.Formatting.None)));
            AssertSanitizedShape(account, actual);
            Assert.AreEqual(expected, actual["location"].Value<string>());
            Assert.AreEqual(expected, actual["properties"]["primaryLocation"].Value<string>());
            Assert.AreEqual(expected, actual["properties"]["secondaryLocation"].Value<string>());
            Assert.IsTrue(JToken.DeepEquals(account["properties"]["keyCreationTime"], actual["properties"]["keyCreationTime"]));
            Assert.IsFalse(fixture.IgnoredQueryParameters.Contains("api-version"));
        }

        [Test]
        public void GenericTagKeysAndPublicSchemaTextRemainDistinct()
        {
            var fixture = new StorageKeyCreationTimeRecordedTests(IsAsync, RecordedTestMode.Playback);
            JObject account = ParseSanitizerJson(AccountJson(AccountId, AllTimestamps));
            account["tags"] = JObject.Parse("""
                {"test":"synthetic-private-value","example-resource-usage":"synthetic-private-value",
                 "example-automation-created-by":"synthetic-private-value","other-automation-created-by":"synthetic-private-value"}
                """);
            account["kind"] = "StorageV2";
            account["sku"] = JObject.Parse("""{"name":"Standard_LRS","tier":"Standard"}""");
            account["properties"]["azureFilesIdentityBasedAuthentication"] = JObject.Parse("""
                {"directoryServiceOptions":"AADKERB","activeDirectoryProperties":{"accountType":"Computer"}}
                """);
            JObject actual = ParseSanitizerJson(ApplyBodySanitizers(fixture, account.ToString(Newtonsoft.Json.Formatting.None)));
            AssertSanitizedShape(account, actual);
            var tags = (JObject)actual["tags"];
            CollectionAssert.AreEquivalent(((JObject)account["tags"]).Properties().Select(property => property.Name),
                tags.Properties().Select(property => property.Name));
            Assert.AreEqual(4, tags.Count, "Distinct generic tag labels must not collapse to one placeholder.");
            Assert.IsTrue(tags.Properties().All(property => property.Value.Value<string>() == RecordedTestBase.SanitizeValue));
            Assert.IsTrue(JToken.DeepEquals(account["sku"], actual["sku"]));
            Assert.AreEqual("StorageV2", actual["kind"].Value<string>());
            Assert.IsTrue(JToken.DeepEquals(account["properties"]["azureFilesIdentityBasedAuthentication"],
                actual["properties"]["azureFilesIdentityBasedAuthentication"]));
            Assert.IsTrue(JToken.DeepEquals(account["properties"]["keyCreationTime"], actual["properties"]["keyCreationTime"]));
        }

        [TestCase("synthetic-tool-created-by", 0)]
        [TestCase("synthetic-tool-created-by", 2)]
        [TestCase("synthetic-tool-created-by", 3)]
        [TestCase("synthetic.*[tool]\"\\{created-by}", 0)]
        [TestCase("synthetic.*[tool]\"\\{created-by}", 2)]
        [TestCase("synthetic.*[tool]\"\\{created-by}", 3)]
        public void ExactToolingTagKeyIsRenamedOnlyInsideTags(string sourceKey, int position)
        {
            var fixture = new StorageKeyCreationTimeRecordedTests(IsAsync, RecordedTestMode.Playback, toolingTagKey: sourceKey);
            JObject account = ParseSanitizerJson(AccountJson(AccountId, AllTimestamps));
            var properties = new List<JProperty>
            {
                new JProperty("generic-{first}", "synthetic-private-value"),
                new JProperty(sourceKey + "-similar", "synthetic-private-value"),
                new JProperty("generic-last", JValue.CreateNull())
            };
            properties.Insert(position, new JProperty(sourceKey, "synthetic-private-value"));
            account["tags"] = new JObject(properties);
            account[sourceKey] = "public-schema-text";
            account["schemaText"] = sourceKey;
            using (JsonDocument document = JsonDocument.Parse(account.ToString(Newtonsoft.Json.Formatting.None)))
            {
                Assert.AreEqual(1, StorageKeyCreationTimeValidation.AssertToolingTagKeyCanBeSanitized(document.RootElement, sourceKey));
            }

            JObject actual = ParseSanitizerJson(ApplyBodySanitizers(fixture, account.ToString(Newtonsoft.Json.Formatting.None)));
            AssertSanitizedShape(account, actual, sourceKey);
            var tags = (JObject)actual["tags"];
            Assert.AreEqual(4, tags.Count, "An exact-key replacement must preserve every dictionary entry.");
            Assert.IsNull(tags.Property(sourceKey));
            Assert.AreEqual(RecordedTestBase.SanitizeValue,
                tags[StorageKeyCreationTimeTestEnvironment.SanitizedToolingTagKey].Value<string>());
            Assert.IsNotNull(tags.Property(sourceKey + "-similar"), "Do not replace an entire tooling prefix.");
            Assert.AreEqual("public-schema-text", actual[sourceKey].Value<string>());
            Assert.AreEqual(sourceKey, actual["schemaText"].Value<string>(), "Do not replace ordinary property names or text.");
            Assert.IsTrue(JToken.DeepEquals(account["properties"]["keyCreationTime"], actual["properties"]["keyCreationTime"]));
        }

        [Test]
        public void CanonicalToolingTagIsIdempotentWithoutPrivatePlaybackInput()
        {
            var environment = new StorageKeyCreationTimeTestEnvironment { Mode = RecordedTestMode.Playback };
            Assert.AreEqual(StorageKeyCreationTimeTestEnvironment.SanitizedToolingTagKey, environment.ToolingTagKeyForSanitizer);
            var fixture = new StorageKeyCreationTimeRecordedTests(IsAsync, RecordedTestMode.Playback);
            JObject account = ParseSanitizerJson(AccountJson(AccountId, AllTimestamps));
            account["tags"] = new JObject { [StorageKeyCreationTimeTestEnvironment.SanitizedToolingTagKey] = "Sanitized", ["test"] = "Sanitized" };
            string original = account.ToString(Newtonsoft.Json.Formatting.None);
            string sanitized = ApplyBodySanitizers(fixture, original);
            Assert.AreEqual(sanitized, ApplyBodySanitizers(fixture, sanitized));
            AssertSanitizedShape(account, ParseSanitizerJson(sanitized));
            using JsonDocument document = JsonDocument.Parse(sanitized);
            Assert.AreEqual(1, StorageKeyCreationTimeValidation.AssertToolingTagKeyCanBeSanitized(
                document.RootElement, StorageKeyCreationTimeTestEnvironment.SanitizedToolingTagKey));
        }

        [TestCase("")]
        [TestCase(" ")]
        public void EmptyPrivateToolingTagInputFailsBeforeRequests(string key)
        {
            Assert.Throws<InvalidOperationException>(() =>
                new StorageKeyCreationTimeRecordedTests(IsAsync, RecordedTestMode.Playback, toolingTagKey: key));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        [TestCase("sanitized-tooling-created-by")]
        [TestCase("SANITIZED-TOOLING-CREATED-BY")]
        public void RecordRequiresPrivateInputRatherThanCanonicalPlaybackKey(string key)
        {
            Assert.Throws<InvalidOperationException>(() =>
                StorageKeyCreationTimeRecordedTests.RequireToolingTagKey(key, RecordedTestMode.Record));
        }

        [TestCase("""{"synthetic-tool-created-by":"x","sanitized-tooling-created-by":"y"}""")]
        [TestCase("""{"sanitized-tooling-created-by":"x"}""")]
        [TestCase("""{"SANITIZED-TOOLING-CREATED-BY":"x"}""")]
        [TestCase("""{"SYNTHETIC-TOOL-CREATED-BY":"x"}""")]
        [TestCase("""{"test":"x","TEST":"y"}""")]
        [TestCase("""{"synthetic-tool-created-by":"x","synthetic-tool-created-by":"y"}""")]
        public void ToolingTagCollisionsAndSpellingMismatchesFailWithoutEchoingKeys(string tags)
        {
            const string sourceKey = "synthetic-tool-created-by";
            using JsonDocument account = JsonDocument.Parse("{\"tags\":" + tags + "}");
            AssertionException failure = Assert.Throws<AssertionException>(() =>
                StorageKeyCreationTimeValidation.AssertToolingTagKeyCanBeSanitized(account.RootElement, sourceKey));
            Assert.Less(failure.Message.IndexOf(sourceKey, StringComparison.OrdinalIgnoreCase), 0);
        }

        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void ToolingTagPreflightChecksEveryReadAndRequiresAnObservedKey(int collisionResponse)
        {
            const string sourceKey = "synthetic-tool-created-by";
            string account = AccountJson(AccountId, AllTimestamps);
            string unrelated = AccountJson(StorageAccountResource.CreateResourceIdentifier(SubscriptionId, "other-rg", "otheraccount"), null);
            string rgPath = $"/subscriptions/{SubscriptionId}/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts";
            string subPath = $"/subscriptions/{SubscriptionId}/providers/Microsoft.Storage/storageAccounts";
            string[] bodies =
            {
                account,
                ListJson(unrelated, Endpoint + rgPath.TrimStart('/') + "?api-version=2026-09-01&$skiptoken=rg-page-2"),
                ListJson(account),
                ListJson(unrelated, subPath + "?api-version=2026-09-01&$skiptoken=sub-page-2"),
                ListJson(account)
            };
            if (collisionResponse >= 0)
            {
                for (int i = 0; i < bodies.Length; i++)
                {
                    JObject body = ParseSanitizerJson(bodies[i]);
                    IEnumerable<JObject> accounts = body["value"] is JArray items ? items.Cast<JObject>() : new[] { body };
                    foreach (JObject item in accounts)
                    {
                        item["tags"] = new JObject
                        {
                            [i == collisionResponse ? StorageKeyCreationTimeTestEnvironment.SanitizedToolingTagKey : sourceKey] = "synthetic-private-value"
                        };
                    }
                    bodies[i] = body.ToString(Newtonsoft.Json.Formatting.None);
                }
            }
            var transport = new MethodCheckingTransport(IsAsync, bodies.Select(Response).ToArray());
            ArmClient client = CreateClient(transport, "2026-09-01");
            using (new TestExecutionContext.IsolatedContext())
            {
                Assert.ThrowsAsync<AssertionException>(() => StorageKeyCreationTimeValidation.VerifyReadsAsync(client, AccountId, sourceKey));
            }
            Assert.AreEqual(collisionResponse < 0 ? 5 : collisionResponse + 1, transport.Requests.Count,
                "Check GET, both LISTs and each continuation; stop at a collision, or after enumeration if the input never matched.");
        }

        [Test]
        public async Task SanitizedGetAndPagedListsPreserveTargetAndExactSeptemberVersion()
        {
            const string sourceSubscription = "11111111-1111-1111-1111-111111111111";
            const string sourceGroup = "synthetic-rg";
            const string sourceAccount = "syntheticaccount";
            const string sourceToolingTagKey = "synthetic-tool-created-by";
            var fixture = new StorageKeyCreationTimeRecordedTests(IsAsync, RecordedTestMode.Playback, sourceGroup, sourceAccount, sourceToolingTagKey);
            var secondFixture = new StorageKeyCreationTimeRecordedTests(!IsAsync, RecordedTestMode.Playback, sourceGroup, sourceAccount, sourceToolingTagKey);
            ResourceIdentifier sourceId = StorageAccountResource.CreateResourceIdentifier(sourceSubscription, sourceGroup, sourceAccount);
            JObject targetBody = ParseSanitizerJson(AccountJson(sourceId, AllTimestamps));
            targetBody["tags"] = new JObject { [sourceToolingTagKey] = "synthetic-private-value", ["test"] = "synthetic-private-value" };
            string target = targetBody.ToString(Newtonsoft.Json.Formatting.None);
            string unrelated = AccountJson(StorageAccountResource.CreateResourceIdentifier(sourceSubscription, "synthetic-other-rg", "syntheticother"), null);
            string another = AccountJson(StorageAccountResource.CreateResourceIdentifier(sourceSubscription, sourceGroup, "syntheticthird"), null);
            string placeholderCollision = AccountJson(StorageAccountResource.CreateResourceIdentifier(
                sourceSubscription, sourceGroup, StorageKeyCreationTimeTestEnvironment.SanitizedAccountName), null);
            string rgPath = $"/subscriptions/{sourceSubscription}/resourceGroups/{sourceGroup}/providers/Microsoft.Storage/storageAccounts";
            string subPath = $"/subscriptions/{sourceSubscription}/providers/Microsoft.Storage/storageAccounts";
            string rgNext = Endpoint + rgPath.TrimStart('/') + "?api-version=2026-09-01&$skiptoken=rg-page-2";
            string subNext = Endpoint + subPath.TrimStart('/') + "?api-version=2026-09-01&$skiptoken=sub-page-2";
            string firstPage = "{\"value\":[" + unrelated + "," + placeholderCollision + "],\"nextLink\":" + JsonSerializer.Serialize(rgNext) + "}";
            string[] original = { target, firstPage, ListJson(target), ListJson(another, subNext), ListJson(target) };
            string[] sanitized = original.Select(body => ApplyBodySanitizers(fixture, body)).ToArray();
            CollectionAssert.AreEqual(sanitized, original.Select(body => ApplyBodySanitizers(secondFixture, body)).ToArray(),
                "Both fixture variants must use identical deterministic substitutions.");

            var variables = new[]
            {
                new RecordedVariableOptions().IsSecret(Guid.Empty.ToString()).Apply(sourceSubscription),
                new RecordedVariableOptions().IsSecret(StorageKeyCreationTimeTestEnvironment.SanitizedResourceGroup).Apply(sourceGroup),
                new RecordedVariableOptions().IsSecret(StorageKeyCreationTimeTestEnvironment.SanitizedAccountName).Apply(sourceAccount)
            };
            ResourceIdentifier sanitizedId = StorageAccountResource.CreateResourceIdentifier(variables[0], variables[1], variables[2]);
            Assert.AreEqual(sanitizedId.ToString(), ParseSanitizerJson(sanitized[0])["id"].Value<string>());
            string request = Endpoint + sourceId.ToString().TrimStart('/') + "?api-version=2026-09-01";
            foreach (var sanitizer in fixture.UriRegexSanitizers)
            {
                request = ReplaceSanitizerRegex(request, sanitizer.Regex, sanitizer.Value, sanitizer.GroupForReplace);
            }
            Assert.AreEqual(new Uri(Endpoint, sanitizedId + "?api-version=2026-09-01").ToString(), request);
            Assert.IsFalse(fixture.IgnoredQueryParameters.Contains("api-version"));
            for (int i = 0; i < original.Length; i++)
            {
                AssertSanitizedShape(ParseSanitizerJson(original[i]), ParseSanitizerJson(sanitized[i]), sourceToolingTagKey);
                foreach (string value in new[] { sourceSubscription, sourceGroup, sourceAccount, "syntheticother", "syntheticthird", "synthetic-other-rg", sourceToolingTagKey })
                {
                    Assert.IsFalse(sanitized[i].Contains(value), "Synthetic source identifiers must not remain.");
                }
            }
            Assert.IsTrue(JToken.DeepEquals(ParseSanitizerJson(target)["properties"]["keyCreationTime"],
                ParseSanitizerJson(sanitized[0])["properties"]["keyCreationTime"]));
            var transport = new MethodCheckingTransport(IsAsync, sanitized.Select(Response).ToArray());
            ArmClientOptions options = StorageKeyCreationTimeRecordedTests.CreateReadOnlyOptions(
                Endpoint, "https://management.azure.com/", sanitizedId, "2026-09-01");
            options.Transport = transport;
            options.Retry.MaxRetries = 0;
            ArmClient client = InstrumentClient(new ArmClient(new MockCredential(), sanitizedId.SubscriptionId, options));
            CollectionAssert.AreEqual(new[] { 2, 2 }, await StorageKeyCreationTimeValidation.VerifyReadsAsync(
                client, sanitizedId, StorageKeyCreationTimeTestEnvironment.SanitizedToolingTagKey));
            Assert.AreEqual(5, transport.Requests.Count);
            Assert.IsTrue(transport.Requests.All(request => request.Method == RequestMethod.Get &&
                request.Uri.ToUri().Host == Endpoint.Host && request.Uri.ToUri().Query.Contains("api-version=2026-09-01")));
        }

        [Test]
        public void MetadataSanitizersPreserveObjectsArraysReferencesAndTimestampText()
        {
            var fixture = new StorageKeyCreationTimeRecordedTests(IsAsync, RecordedTestMode.Playback, "synthetic-rg", "syntheticaccount");
            JObject account = ParseSanitizerJson(AccountJson(
                StorageAccountResource.CreateResourceIdentifier("11111111-1111-1111-1111-111111111111", "synthetic-rg", "syntheticaccount"), AllTimestamps));
            const string identityId = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/synthetic-identity-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/syntheticidentity";
            const string tenant = "33333333-3333-3333-3333-333333333333";
            account["identity"] = JObject.Parse($$$$"""
                {"type":"UserAssigned","tenantId":"{{{{tenant}}}}",
                 "userAssignedIdentities":{"{{{{identityId}}}}":{"principalId":"44444444-4444-4444-4444-444444444444","clientId":"55555555-5555-5555-5555-555555555555"}}}
                """);
            account["tags"] = JObject.Parse("""{"purpose":"synthetic-private-tag","empty":""}""");
            JObject properties = (JObject)account["properties"];
            properties["networkAcls"] = JObject.Parse("""
                {"defaultAction":"Deny","ipRules":[{"value":"198.51.100.0/24","action":"Allow"}],
                 "ipv6Rules":[{"value":"2001:db8:abcd::/64","action":"Allow"}],
                 "virtualNetworkRules":[{"id":"/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/synthetic-network-rg/providers/Microsoft.Network/virtualNetworks/syntheticvnet/subnets/syntheticsubnet","action":"Allow"}],
                 "resourceAccessRules":[{"tenantId":"33333333-3333-3333-3333-333333333333","resourceId":"/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/synthetic-access-rg/providers/Microsoft.Synapse/workspaces/syntheticworkspace"}]}
                """);
            properties["azureFilesIdentityBasedAuthentication"] = JObject.Parse("""
                {"directoryServiceOptions":"AD","activeDirectoryProperties":{"domainName":"synthetic.example","forestName":"forest.synthetic.example",
                 "netBiosDomainName":"SYNTHETIC","domainGuid":"66666666-6666-6666-6666-666666666666",
                 "domainSid":"S-1-5-21-111-222-333","azureStorageSid":"S-1-5-21-111-222-333-444","samAccountName":"synthetic-sam","accountType":"Computer"}}
                """);
            properties["encryption"] = ParseSanitizerJson($$$$"""
                {"identity":{"userAssignedIdentity":"{{{{identityId}}}}","federatedIdentityClientId":"77777777-7777-7777-7777-777777777777"},
                 "keyvaultproperties":{"keyname":"synthetic-key","keyversion":"synthetic-version","keyvaulturi":"https://synthetic-vault.example/",
                 "currentVersionedKeyIdentifier":"https://synthetic-vault.example/keys/synthetic-key/synthetic-version",
                 "lastKeyRotationTimestamp":"2026-10-13T01:02:03.1200000Z"}}
                """);
            properties["privateEndpointConnections"] = JObject.Parse("""
                {"items":[{"name":"synthetic-connection",
                 "id":"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/synthetic-rg/providers/Microsoft.Storage/storageAccounts/syntheticaccount/privateEndpointConnections/synthetic-connection",
                 "properties":{"privateEndpoint":{"id":"/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/synthetic-network-rg/providers/Microsoft.Network/privateEndpoints/synthetic-private-endpoint"},
                 "privateLinkServiceConnectionState":{"status":"Approved","description":"synthetic-private-description","actionRequired":"None"}}}]}
                """)["items"];
            properties["primaryEndpoints"] = JObject.Parse("""{"blob":"https://syntheticaccount.blob.example/","microsoftEndpoints":{"file":"https://syntheticaccount.file.example/"}}""");
            properties["customDomain"] = JObject.Parse("""{"name":"synthetic-custom.example","useSubDomainName":false}""");
            properties["unknownEndpoint"] = "https://synthetic-host.example/safe";
            properties["armEndpoint"] = Endpoint.ToString();
            properties["audience"] = "https://management.azure.com/";
            properties["unknownOptional"] = null;
            string sanitized = ApplyBodySanitizers(fixture, account.ToString(Newtonsoft.Json.Formatting.None));
            JObject actual = ParseSanitizerJson(sanitized);
            AssertSanitizedShape(account, actual);
            foreach (string value in new[]
            {
                "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222", tenant,
                "44444444-4444-4444-4444-444444444444", "55555555-5555-5555-5555-555555555555", "66666666-6666-6666-6666-666666666666",
                "77777777-7777-7777-7777-777777777777",
                "synthetic", "SYNTHETIC", "198.51.100.0", "2001:db8:abcd", "S-1-5-21-111"
            })
            {
                Assert.IsFalse(sanitized.Contains(value), "Synthetic sensitive metadata must be replaced.");
            }
            var identities = (JObject)actual["identity"]["userAssignedIdentities"];
            Assert.AreEqual(1, identities.Count);
            Assert.AreEqual(identities.Properties().Single().Name, actual["properties"]["encryption"]["identity"]["userAssignedIdentity"].Value<string>());
            Assert.AreEqual(Endpoint.ToString(), actual["properties"]["armEndpoint"].Value<string>());
            Assert.AreEqual("https://management.azure.com/", actual["properties"]["audience"].Value<string>());
            Assert.AreEqual("https://sanitized.example/safe", actual["properties"]["unknownEndpoint"].Value<string>());
            Assert.IsTrue(JToken.DeepEquals(properties["keyCreationTime"], actual["properties"]["keyCreationTime"]));
            Assert.AreEqual("2026-10-13T01:02:03.1200000Z", actual["properties"]["encryption"]["keyvaultproperties"]["lastKeyRotationTimestamp"].Value<string>());
            using JsonDocument document = JsonDocument.Parse(sanitized);
            var model = ModelReaderWriter.Read<StorageAccountData>(BinaryData.FromString(sanitized));
            StorageKeyCreationTimeValidation.AssertTimestamps(document.RootElement, model.KeyCreationTime, requireFeature: true);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void MultipleIdentityKeysFailBeforeLossySanitization(int count)
        {
            JObject account = ParseSanitizerJson(AccountJson(AccountId, AllTimestamps));
            var identities = new JObject();
            for (int i = 0; i < count; i++)
            {
                identities[$"/subscriptions/{SubscriptionId}/resourceGroups/identity-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/identity{i}"] = new JObject();
            }
            account["identity"] = new JObject { ["userAssignedIdentities"] = identities };
            using JsonDocument document = JsonDocument.Parse(account.ToString());
            if (count <= 1)
            {
                Assert.DoesNotThrow(() => StorageKeyCreationTimeValidation.AssertSanitizableIdentity(document.RootElement));
            }
            else
            {
                Assert.Throws<AssertionException>(() => StorageKeyCreationTimeValidation.AssertSanitizableIdentity(document.RootElement));
            }
        }

        private static JObject ParseSanitizerJson(string json) =>
            JObject.Load(new Newtonsoft.Json.JsonTextReader(new StringReader(json)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None },
                new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });

        // Exercise the configured standard rules on synthetic payloads only; this does not rewrite recording assets.
        private static string ApplyBodySanitizers(StorageKeyCreationTimeRecordedTests fixture, string json)
        {
            JObject body = ParseSanitizerJson(json);
            foreach (string path in fixture.JsonPathSanitizers)
            {
                foreach (JToken token in body.SelectTokens(path).ToArray())
                {
                    if (token.Type == JTokenType.String)
                    {
                        token.Replace(RecordedTestBase.SanitizeValue);
                    }
                }
            }
            foreach (var sanitizer in fixture.BodyKeySanitizers)
            {
                foreach (JToken token in body.SelectTokens(sanitizer.JsonPath).ToArray())
                {
                    if (token.Type == JTokenType.Null)
                    {
                        continue;
                    }
                    Assert.AreEqual(JTokenType.String, token.Type, "Sanitizers must only target string leaves.");
                    token.Replace(sanitizer.Regex == null ? sanitizer.Value :
                        ReplaceSanitizerRegex(token.Value<string>(), sanitizer.Regex, sanitizer.Value, sanitizer.GroupForReplace));
                }
            }
            string result = body.ToString(Newtonsoft.Json.Formatting.None);
            foreach (var sanitizer in fixture.BodyRegexSanitizers)
            {
                result = ReplaceSanitizerRegex(result, sanitizer.Regex, sanitizer.Value, sanitizer.GroupForReplace);
            }
            return result;
        }

        private static string ReplaceSanitizerRegex(string value, string pattern, string replacement, string group) =>
            Regex.Replace(value, pattern, match =>
            {
                if (group == null)
                {
                    return replacement;
                }
                Group capture = match.Groups[group];
                int start = capture.Index - match.Index;
                return match.Value.Remove(start, capture.Length).Insert(start, replacement);
            });

        private static void AssertSanitizedShape(JToken original, JToken sanitized, string toolingTagKey = null)
        {
            Assert.AreEqual(original.Type, sanitized.Type, "Sanitization must preserve JSON types, including null.");
            if (original is JProperty property && !property.Name.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
            {
                bool selectedTag = toolingTagKey != null && property.Name == toolingTagKey &&
                    property.Parent?.Parent is JProperty dictionary && dictionary.Name == "tags";
                string expected = selectedTag ? StorageKeyCreationTimeTestEnvironment.SanitizedToolingTagKey : property.Name;
                Assert.AreEqual(expected, ((JProperty)sanitized).Name, "Only resource-ID keys and the selected exact tag key may be renamed.");
            }
            if (original is JContainer container)
            {
                JToken[] before = container.Children().ToArray();
                JToken[] after = ((JContainer)sanitized).Children().ToArray();
                Assert.AreEqual(before.Length, after.Length, "Sanitization must preserve fields and array/account counts.");
                for (int i = 0; i < before.Length; i++)
                {
                    AssertSanitizedShape(before[i], after[i], toolingTagKey);
                }
            }
        }

        [Test]
        public void ModelPreservesAllValuesAndExcludesReadOnlyFieldsFromRequests()
        {
            var model = ModelReaderWriter.Read<StorageAccountKeyCreationTime>(BinaryData.FromString(AllTimestamps));
            using JsonDocument account = JsonDocument.Parse(AccountJson(AccountId, AllTimestamps));
            StorageKeyCreationTimeValidation.AssertTimestamps(account.RootElement, model, requireFeature: true);

            foreach (string name in new[] { "Kerb1", "Kerb2", "Keytab1", "Keytab2" })
            {
                var property = typeof(StorageAccountKeyCreationTime).GetProperty(name);
                Assert.AreEqual(typeof(DateTimeOffset?), property.PropertyType);
                Assert.IsFalse(property.CanWrite);
            }
            Assert.IsTrue(typeof(StorageAccountKeyCreationTime).GetProperty(nameof(model.Key1)).CanWrite);
            Assert.IsTrue(typeof(StorageAccountKeyCreationTime).GetProperty(nameof(model.Key2)).CanWrite);

            using JsonDocument wire = JsonDocument.Parse(ModelReaderWriter.Write(model, new ModelReaderWriterOptions("W")));
            CollectionAssert.AreEquivalent(new[] { "key1", "key2" }, wire.RootElement.EnumerateObject().Select(property => property.Name));
            using JsonDocument persisted = JsonDocument.Parse(ModelReaderWriter.Write(model, ModelReaderWriterOptions.Json));
            Assert.AreEqual(6, persisted.RootElement.EnumerateObject().Count());
            var roundTrip = ModelReaderWriter.Read<StorageAccountKeyCreationTime>(BinaryData.FromString(persisted.RootElement.GetRawText()));
            StorageKeyCreationTimeValidation.AssertTimestamps(account.RootElement, roundTrip, requireFeature: true);

            var oldFactory = ArmStorageModelFactory.StorageAccountKeyCreationTime(model.Key1, model.Key2);
            Assert.AreEqual(model.Key1, oldFactory.Key1);
            Assert.AreEqual(model.Key2, oldFactory.Key2);
            Assert.IsNull(oldFactory.Kerb1);
            var newFactory = ArmStorageModelFactory.StorageAccountKeyCreationTime(model.Key1, model.Key2, model.Kerb1, model.Kerb2, model.Keytab1, model.Keytab2);
            StorageKeyCreationTimeValidation.AssertTimestamps(account.RootElement, newFactory, requireFeature: true);
        }

        [TestCase("POST")]
        [TestCase("PUT")]
        [TestCase("PATCH")]
        [TestCase("DELETE")]
        [TestCase("HEAD")]
        public void GuardRejectsNonGetBeforeTransport(string method)
        {
            var transport = new MockTransport();
            HttpPipeline pipeline = CreatePipeline(transport);
            using HttpMessage message = pipeline.CreateMessage();
            message.Request.Method = new RequestMethod(method);
            message.Request.Uri.Reset(new Uri(Endpoint, AccountId + "?api-version=2026-12-01"));
            Assert.Throws<InvalidOperationException>(() => pipeline.Send(message, default));
            Assert.IsEmpty(transport.Requests);
        }

        [TestCase("https://other.example/", "", "2026-12-01")]
        [TestCase("https://management.azure.com/", "", "2026-12-01")]
        [TestCase("https://eastus2euap.management.azure.com/", "/listKeys", "2026-12-01")]
        [TestCase("https://eastus2euap.management.azure.com/", "", "2026-09-01")]
        [TestCase("https://eastus2euap.management.azure.com/", "", "2026-12-01&api-version=2026-12-01")]
        [TestCase("https://eastus2euap.management.azure.com/", "", "")]
        public void GuardRejectsUnexpectedHostPathOrVersion(string host, string suffix, string version)
        {
            var transport = new MockTransport();
            HttpPipeline pipeline = CreatePipeline(transport);
            using HttpMessage message = pipeline.CreateMessage();
            message.Request.Method = RequestMethod.Get;
            message.Request.Uri.Reset(new Uri(new Uri(host), AccountId + suffix + "?api-version=" + version));
            Assert.Throws<InvalidOperationException>(() => pipeline.Send(message, default));
            Assert.IsEmpty(transport.Requests);
        }

        [TestCase("")]
        [TestCase("2026-06-01")]
        [TestCase("latest")]
        public void UnsupportedApiVersionOverrideIsRejected(string apiVersion)
        {
            Assert.Throws<ArgumentException>(() =>
                StorageKeyCreationTimeRecordedTests.CreateReadOnlyOptions(Endpoint, "https://management.azure.com/", AccountId, apiVersion));
        }

        [TestCase("POST", "2026-09-01", "")]
        [TestCase("DELETE", "2026-09-01", "")]
        [TestCase("GET", "2026-12-01", "")]
        [TestCase("GET", "2026-09-01&api-version=2026-09-01", "")]
        [TestCase("GET", "2026-09-01", "/listKeys")]
        public void SeptemberOverrideDoesNotWeakenRequestGuard(string method, string version, string suffix)
        {
            var transport = new MockTransport();
            var options = new ArmClientOptions { Transport = transport };
            HttpPipeline pipeline = HttpPipelineBuilder.Build(options,
                new StorageKeyCreationTimeReadOnlyPolicy(Endpoint, AccountId, "2026-09-01"));
            using HttpMessage message = pipeline.CreateMessage();
            message.Request.Method = new RequestMethod(method);
            message.Request.Uri.Reset(new Uri(Endpoint, AccountId + suffix + "?api-version=" + version));
            Assert.Throws<InvalidOperationException>(() => pipeline.Send(message, default));
            Assert.IsEmpty(transport.Requests);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("false")]
        public void BootstrappingMustBeExplicitlyDisabled(string setting)
        {
            Assert.Throws<InvalidOperationException>(() => StorageKeyCreationTimeRecordedTests.RequireBootstrappingDisabled(setting));
        }

        [Test]
        public void BootstrappingDisabledSettingIsAccepted()
        {
            Assert.DoesNotThrow(() => StorageKeyCreationTimeRecordedTests.RequireBootstrappingDisabled("true"));
        }

        private ArmClient CreateClient(MockTransport transport, string apiVersion = null)
        {
            ArmClientOptions options = StorageKeyCreationTimeRecordedTests.CreateReadOnlyOptions(Endpoint, "https://management.azure.com/", AccountId, apiVersion);
            options.Transport = transport;
            options.Retry.MaxRetries = 0;
            var credential = new MockCredential
            {
                GetTokenCallback = (context, cancellationToken) =>
                    CollectionAssert.AreEqual(new[] { "https://management.azure.com/.default" }, context.Scopes)
            };
            return InstrumentClient(new ArmClient(credential, SubscriptionId, options));
        }

        private static HttpPipeline CreatePipeline(MockTransport transport)
        {
            var options = new ArmClientOptions { Transport = transport };
            return HttpPipelineBuilder.Build(options, new StorageKeyCreationTimeReadOnlyPolicy(Endpoint, AccountId));
        }

        private static MockResponse Response(string json) => new MockResponse(200)
            .AddHeader("Content-Type", "application/json").SetContent(json);

        private static string AccountJson(ResourceIdentifier id, string timestamps) =>
            "{\"id\":" + JsonSerializer.Serialize(id.ToString()) + ",\"name\":" + JsonSerializer.Serialize(id.Name) +
            ",\"type\":\"Microsoft.Storage/storageAccounts\",\"location\":\"eastus\",\"properties\":{" +
            (timestamps == null ? string.Empty : "\"keyCreationTime\":" + timestamps) + "}}";

        private static string ListJson(string account, string nextLink = null) =>
            "{\"value\":[" + account + "]" + (nextLink == null ? string.Empty : ",\"nextLink\":" + JsonSerializer.Serialize(nextLink)) + "}";

        private sealed class MethodCheckingTransport : MockTransport
        {
            private readonly bool _isAsync;

            internal MethodCheckingTransport(bool isAsync, params MockResponse[] responses) : base(responses)
            {
                _isAsync = isAsync;
            }

            public override void Process(HttpMessage message)
            {
                Assert.IsFalse(_isAsync, "The sync variant must dispatch synchronously.");
                base.Process(message);
            }

            public override ValueTask ProcessAsync(HttpMessage message)
            {
                Assert.IsTrue(_isAsync, "The async variant must dispatch asynchronously.");
                return base.ProcessAsync(message);
            }
        }
    }
}
