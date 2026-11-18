// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using Azure.Core;
using Azure.Core.TestFramework;
using Azure.Identity;

namespace Azure.ResourceManager.Storage.Tests
{
    public class StorageKeyCreationTimeTestEnvironment : StorageManagementTestEnvironment
    {
        internal const string SanitizedAccountName = "targetaccount";
        internal const string SanitizedResourceGroup = "target-rg";
        internal const string SanitizedToolingTagKey = "sanitized-tooling-created-by";
        private TokenCredential _credential;

        public string TargetSubscriptionId => GetRecordedVariable("KEY_CREATION_TIME_SUBSCRIPTION_ID", options => options.IsSecret(Guid.Empty.ToString()));
        public string TargetResourceGroup => GetRecordedVariable("KEY_CREATION_TIME_RESOURCE_GROUP", options => options.IsSecret(SanitizedResourceGroup));
        public string TargetAccountName => GetRecordedVariable("KEY_CREATION_TIME_ACCOUNT_NAME", options => options.IsSecret(SanitizedAccountName));
        internal string ResourceGroupForSanitizer => Mode == RecordedTestMode.Playback ? SanitizedResourceGroup : GetVariable("KEY_CREATION_TIME_RESOURCE_GROUP");
        internal string AccountNameForSanitizer => Mode == RecordedTestMode.Playback ? SanitizedAccountName : GetVariable("KEY_CREATION_TIME_ACCOUNT_NAME");
        internal string ToolingTagKeyForSanitizer => Mode == RecordedTestMode.Playback
            ? SanitizedToolingTagKey
            : Environment.GetEnvironmentVariable("STORAGE_KEY_CREATION_TIME_TOOLING_TAG_KEY");
        public string TargetEndpoint => GetRecordedVariable("KEY_CREATION_TIME_ARM_ENDPOINT");
        public string TargetAudience => GetRecordedVariable("KEY_CREATION_TIME_ARM_AUDIENCE");
        public string TargetApiVersion => StorageKeyCreationTimeReadOnlyPolicy.ResolveApiVersion(
            GetRecordedOptionalVariable("KEY_CREATION_TIME_API_VERSION"));

        public void RequireRecordingApproval()
        {
            RequireApproval("KEY_CREATION_TIME_CANARY_APPROVED");
            RequireApproval("KEY_CREATION_TIME_LIST_RECORDING_APPROVED");
        }

        private void RequireApproval(string name)
        {
            if (!bool.TryParse(GetOptionalVariable(name), out bool approved) || !approved)
            {
                throw new InvalidOperationException($"Explicit approval is required: STORAGE_{name}=true.");
            }
        }

        public override TokenCredential Credential => Mode == RecordedTestMode.Playback
            ? base.Credential
            : _credential ??= new AzureCliCredential(new AzureCliCredentialOptions
            {
                TenantId = GetVariable("KEY_CREATION_TIME_TENANT_ID")
            });
    }
}
