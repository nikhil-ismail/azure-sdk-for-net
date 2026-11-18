// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Core;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Storage.Models;
using NUnit.Framework;

namespace Azure.ResourceManager.Storage.Tests
{
    internal static class StorageKeyCreationTimeValidation
    {
        internal static void AssertTimestamps(JsonElement account, StorageAccountKeyCreationTime actual, bool requireFeature)
        {
            AssertSanitizableIdentity(account);
            JsonElement timestamps = default;
            bool hasTimestamps = account.TryGetProperty("properties", out JsonElement properties) &&
                properties.TryGetProperty("keyCreationTime", out timestamps) && timestamps.ValueKind != JsonValueKind.Null;
            if (!hasTimestamps)
            {
                Assert.IsNull(actual, "An omitted/null keyCreationTime object must remain null.");
                Assert.IsFalse(requireFeature, "The target must expose at least one new timestamp; all-null responses are not feature evidence.");
                return;
            }

            Assert.IsNotNull(actual, "The SDK must deserialize the keyCreationTime object.");
            string[] names = { "key1", "key2", "kerb1", "kerb2", "keytab1", "keytab2" };
            DateTimeOffset?[] values = { actual.Key1, actual.Key2, actual.Kerb1, actual.Kerb2, actual.Keytab1, actual.Keytab2 };
            int featureValues = 0;
            for (int i = 0; i < names.Length; i++)
            {
                DateTimeOffset? expected = timestamps.TryGetProperty(names[i], out JsonElement timestamp) &&
                    timestamp.ValueKind != JsonValueKind.Null ? timestamp.GetDateTimeOffset() : null;
                Assert.AreEqual(expected, values[i], $"Typed {names[i]} must match the corresponding response JSON, including absence/null.");
                if (expected.HasValue)
                {
                    Assert.IsTrue(values[i].Value.EqualsExact(expected.Value), $"{names[i]} must preserve precision and offset.");
                    if (i >= 2)
                    {
                        Assert.Greater(expected.Value, new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero),
                            $"{names[i]} must be a meaningful creation time, not a default/sentinel timestamp.");
                        featureValues++;
                    }
                }
            }
            if (requireFeature)
            {
                Assert.Greater(featureValues, 0, "The target must expose at least one new timestamp; all-null comparisons are not feature evidence.");
            }
        }

        internal static void AssertSanitizableIdentity(JsonElement account)
        {
            if (account.TryGetProperty("identity", out JsonElement identity) &&
                identity.ValueKind == JsonValueKind.Object &&
                identity.TryGetProperty("userAssignedIdentities", out JsonElement identities) &&
                identities.ValueKind == JsonValueKind.Object)
            {
                Assert.LessOrEqual(identities.EnumerateObject().Count(), 1,
                    "Fixed name sanitizers would merge multiple user-assigned identity dictionary keys. Stop recording and obtain a distinct replacement plan; do not remove identities.");
            }
        }

        internal static int AssertToolingTagKeyCanBeSanitized(JsonElement account, string toolingTagKey)
        {
            if (!account.TryGetProperty("tags", out JsonElement tags) || tags.ValueKind == JsonValueKind.Null)
            {
                return 0;
            }
            Assert.AreEqual(JsonValueKind.Object, tags.ValueKind, "Account tags must remain a JSON dictionary.");
            const string replacement = StorageKeyCreationTimeTestEnvironment.SanitizedToolingTagKey;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int matches = 0;
            foreach (JsonProperty tag in tags.EnumerateObject())
            {
                bool selected = tag.Name.Equals(toolingTagKey, StringComparison.Ordinal);
                Assert.IsFalse(!selected && tag.Name.Equals(toolingTagKey, StringComparison.OrdinalIgnoreCase),
                    "The private tooling tag key has a different spelling or case. Stop and review the exact process-local input.");
                Assert.IsFalse(!toolingTagKey.Equals(replacement, StringComparison.Ordinal) &&
                    tag.Name.Equals(replacement, StringComparison.OrdinalIgnoreCase),
                    "The reserved sanitized tooling tag key already exists. Stop rather than merging distinct tag keys.");
                Assert.IsTrue(names.Add(selected ? replacement : tag.Name),
                    "Tag-key sanitization would collide or the response contains duplicate tag keys. No tags may be removed or overwritten.");
                if (selected)
                {
                    matches++;
                }
            }
            return matches;
        }

        internal static async Task<int[]> VerifyReadsAsync(ArmClient client, ResourceIdentifier accountId, string toolingTagKey = null)
        {
            int toolingTagMatches = 0;
            StorageAccountResource target = client.GetStorageAccountResource(accountId);
            Response<StorageAccountResource> response = await target.GetAsync();
            Assert.AreEqual(200, response.GetRawResponse().Status);
            Assert.AreEqual(accountId, response.Value.Id);
            using (JsonDocument document = JsonDocument.Parse(response.GetRawResponse().Content))
            {
                if (toolingTagKey != null)
                {
                    toolingTagMatches += AssertToolingTagKeyCanBeSanitized(document.RootElement, toolingTagKey);
                }
                AssertTimestamps(document.RootElement, response.Value.Data.KeyCreationTime, requireFeature: true);
            }

            StorageAccountKeyCreationTime expected = response.Value.Data.KeyCreationTime;
            StorageAccountCollection accounts = client.GetResourceGroupResource(
                ResourceGroupResource.CreateResourceIdentifier(accountId.SubscriptionId, accountId.ResourceGroupName)).GetStorageAccounts();
            SubscriptionResource subscription = client.GetSubscriptionResource(
                SubscriptionResource.CreateResourceIdentifier(accountId.SubscriptionId));
            int[] pages = new int[2];
            for (int scope = 0; scope < pages.Length; scope++)
            {
                int matches = 0;
                void VerifyPage(Page<StorageAccountResource> page)
                {
                    pages[scope]++;
                    Assert.AreEqual(200, page.GetRawResponse().Status);
                    using JsonDocument document = JsonDocument.Parse(page.GetRawResponse().Content);
                    JsonElement items = document.RootElement.GetProperty("value");
                    Assert.AreEqual(items.GetArrayLength(), page.Values.Count, "Every account on the page must be deserialized.");
                    for (int i = 0; i < page.Values.Count; i++)
                    {
                        StorageAccountResource account = page.Values[i];
                        Assert.AreEqual(new ResourceIdentifier(items[i].GetProperty("id").GetString()), account.Id);
                        if (toolingTagKey != null)
                        {
                            toolingTagMatches += AssertToolingTagKeyCanBeSanitized(items[i], toolingTagKey);
                        }
                        bool isTarget = account.Id == accountId;
                        AssertTimestamps(items[i], account.Data.KeyCreationTime, requireFeature: isTarget);
                        if (isTarget)
                        {
                            matches++;
                            CollectionAssert.AreEqual(
                                new[] { expected.Key1, expected.Key2, expected.Kerb1, expected.Kerb2, expected.Keytab1, expected.Keytab2 },
                                new[] { account.Data.KeyCreationTime.Key1, account.Data.KeyCreationTime.Key2, account.Data.KeyCreationTime.Kerb1,
                                    account.Data.KeyCreationTime.Kerb2, account.Data.KeyCreationTime.Keytab1, account.Data.KeyCreationTime.Keytab2 },
                                "GET and LIST timestamps for the approved target must agree. Avoid key rotation during recording.");
                        }
                    }
                }

                AsyncPageable<StorageAccountResource> list = scope == 0 ? accounts.GetAllAsync() : subscription.GetStorageAccountsAsync();
                await foreach (Page<StorageAccountResource> page in list.AsPages())
                {
                    VerifyPage(page);
                }
                Assert.AreEqual(1, matches, "Each fully enumerated LIST must contain the approved account exactly once.");
            }
            if (toolingTagKey != null)
            {
                Assert.Greater(toolingTagMatches, 0,
                    "The configured tooling tag key was not found in any approved response. Stop and review the private input rather than saving an unverified sanitization.");
            }
            return pages;
        }
    }
}
