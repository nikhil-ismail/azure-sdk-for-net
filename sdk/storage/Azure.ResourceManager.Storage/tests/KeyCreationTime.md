# Read-only key creation timestamp validation

`StorageKeyCreationTimeRecordedTests.GetAndListsExposeKeyCreationTimestamps` runs
in both sync and async variants. It reads an approved existing account, then fully
enumerates the account lists for its resource group and subscription.

The target must expose at least one of `kerb1`, `kerb2`, `keytab1`, or `keytab2`.
Each typed timestamp, including `key1` and `key2`, must match its response JSON.
The target's GET and both LIST results must agree. Avoid key rotation during
recording. Other accounts may omit timestamps. Missing/null fields and forced
multi-page lists are covered by offline unit tests; no accounts need to be created
to force live pagination.

## Safety and approval prerequisites

- Use only Record or Playback. Live mode is rejected.
- The target GET route and CLI authentication are confirmed: regional ARM
  `https://eastus2euap.management.azure.com` with public ARM audience
  `https://management.azure.com/`, without special routing headers. SRP logs
  correlated the manual GET with the intended Canary deployment. The initial
  approved Record run subsequently passed GET and both LIST scopes in sync and
  async variants, with September requests.
- API `2026-12-01` is currently rejected by both global and regional ARM.
  December deployment is pending until after CCOA; actual December live
  validation is deferred, not passed.
- Nate/Sean approved September requests for the temporary Canary validation
  window. The user applied the regional
  `KeyCreationTimeKeytabExposureMinApiVersion=2026-09-01` DC override;
  `EnableKeyCreationTimeKeytabExposure` is true. Manual September GET returned
  all four new timestamps. Do not hardcode those observed values into tests.
- DC rollout completion across all slices was confirmed for the initial run.
  Restore the temporary regional DC override after the agreed validation window;
  this test does not change or restore service configuration.
- Target GET and both complete LIST scopes succeeded in the initial approved
  live run. Recheck permissions and routing during any approved re-record.
  Subscription LIST can capture unrelated accounts; obtain the subscription and
  resource-group owners' approval before recording, or use an approved isolated
  subscription. Do not assign roles from the test.
- Approval for the initial local recording does not authorize another live run
  or publication. Obtain separate re-record approval before setting the recording
  approval flags again.

## Temporary request-version exception

The TypeSpec contract remains December-only and the generated SDK remains
December-based, pinned to `4fd0da26994e2a2bf0fb9a8124dada724a8c55f9`.
There is no SDK regeneration or change to September contracts for this exception.

Without an override, test requests use `2026-12-01`. Set
`STORAGE_KEY_CREATION_TIME_API_VERSION=2026-09-01` explicitly for the approved
Canary run. Only these two exact versions are accepted; empty or unsupported
values fail. The selected version is applied through `ArmClientOptions.SetApiVersion`
for account GET, both LISTs, and continuation requests, and is checked by the
read-only guard. There is no automatic downgrade.

The override is saved as a recording variable. Playback selects the recorded
value, not the current machine's override, and retains exact `api-version`
matching. September recordings validate the December-generated typed client
against the approved September request exception; they do not prove that the
December endpoint or September specification contracts support the new fields.
Use separate, clearly identified recording assets/results if December is recorded
later: this scenario's recording filenames are shared and a new recording can
replace its existing sync/async sessions.

The fixture uses the shared `RecordedTestBase`, Storage test environment,
management interceptor, and test proxy, rather than `StorageManagementTestBase`
or its account fixtures. It has no resource setup/cleanup hooks and does not
attach resource-group cleanup, expiration, or delete-after policies.
`AZURE_DISABLE_BOOTSTRAPPING=true` is required before fixture construction to
disable shared environment bootstrapping and expiration PATCHes.

A pipeline guard rejects non-GET methods, other hosts/subscriptions/resource
paths, and any API version other than the exact selected version. The only allowed paths are the
target account GET and its RG/subscription account LIST paths, including their
continuation requests. It runs before dispatch and again on each retry.

## Local configuration for an approved Record run

Use process environment variables, not tracked configuration. The `STORAGE_`
prefix takes priority over shared test-resource environment files.
Before a separately approved run, also supply the private exact tag-key input
using the local-only procedure below. The fixture fails before requests if that
input is empty; it is deliberately not loaded from test-resource files.

```powershell
# CLI login for the confirmed tenant/subscription has already succeeded.
# Set target identifiers locally; do not put them in tracked configuration.

$env:AZURE_TEST_MODE = 'Record'
$env:AZURE_DISABLE_BOOTSTRAPPING = 'true'
$env:STORAGE_KEY_CREATION_TIME_TENANT_ID = '<tenant-guid>'
$env:STORAGE_KEY_CREATION_TIME_SUBSCRIPTION_ID = '<subscription-guid>'
$env:STORAGE_KEY_CREATION_TIME_RESOURCE_GROUP = '<approved-rg>'
$env:STORAGE_KEY_CREATION_TIME_ACCOUNT_NAME = '<approved-existing-account>'
$env:STORAGE_KEY_CREATION_TIME_ARM_ENDPOINT = 'https://eastus2euap.management.azure.com'
$env:STORAGE_KEY_CREATION_TIME_ARM_AUDIENCE = 'https://management.azure.com/'
$env:STORAGE_KEY_CREATION_TIME_API_VERSION = '2026-09-01'
# Set these only after DC rollout completion and full LIST capture are approved.
$env:STORAGE_KEY_CREATION_TIME_CANARY_APPROVED = 'true'
$env:STORAGE_KEY_CREATION_TIME_LIST_RECORDING_APPROVED = 'true'

dotnet test .\sdk\storage\Azure.ResourceManager.Storage\tests\Azure.ResourceManager.Storage.Tests.csproj `
  -f net10.0 `
  --filter 'FullyQualifiedName~StorageKeyCreationTimeRecordedTests&FullyQualifiedName~GetAndListsExposeKeyCreationTimestamps' `
  --logger 'console;verbosity=normal' `
  --logger 'trx;LogFileName=key-creation-time-rerecord.trx'
```

### Private exact tag-key input

The tooling-prefixed creator tag key is treated as internal. Do not copy its
value into source, synthetic fixtures, shell command literals/history, environment
files, runsettings, chat or logs. It is not a recorded variable. Only Record
requires `STORAGE_KEY_CREATION_TIME_TOOLING_TAG_KEY`; Playback uses the canonical
`sanitized-tooling-created-by` key and needs no private input.

Before re-recording, if the local assets still contain the original key, select
the single creator-label key privately from both complete recordings. The
consolidated recordings are now sanitized, so this procedure cannot recover
their original key; use the masked prompt below for any later approved Record.
This reads local JSON only and prints no values.
The suffix is used only to discover the private input, not as the sanitizer's
matching rule. If selection is ambiguous or empty, stop for local review.

```powershell
$recordingDirectory = '<local-directory-containing-both-current-recordings>'
$privateKeys = @(foreach ($name in @(
    'GetAndListsExposeKeyCreationTimestamps.json',
    'GetAndListsExposeKeyCreationTimestampsAsync.json'
)) {
    $record = Get-Content -LiteralPath (Join-Path $recordingDirectory $name) -Raw |
        ConvertFrom-Json -AsHashtable
    foreach ($entry in $record.Entries) {
        $body = $entry.ResponseBody
        $accounts = if ($body.ContainsKey('value')) { $body.value } else { @($body) }
        foreach ($account in $accounts) {
            if ($account.tags -is [System.Collections.IDictionary]) {
                foreach ($key in $account.tags.Keys) {
                    if (($key -replace '[^a-zA-Z0-9]', '').EndsWith(
                        'azurecreatedby', [StringComparison]::OrdinalIgnoreCase)) {
                        $key
                    }
                }
            }
        }
    }
}) | Sort-Object -Unique -CaseSensitive
$privateKeys = @($privateKeys)
if ($privateKeys.Count -ne 1) { throw 'Private tag-key selection requires local review.' }
$env:STORAGE_KEY_CREATION_TIME_TOOLING_TAG_KEY = $privateKeys[0]
```

For later recordings after the local assets have been sanitized, the real key
cannot be recovered from those assets. Supply it privately in the terminal
instead (PowerShell 7.1+), without a command-history literal:

```powershell
$env:STORAGE_KEY_CREATION_TIME_TOOLING_TAG_KEY = Read-Host 'Private exact tooling tag key' -MaskInput
```

Keep that value only in the process environment inherited by the test process.
Do not enable environment/configuration dumps or proxy debug logging.
After the separately approved run, clear it with
`Remove-Item Env:\STORAGE_KEY_CREATION_TIME_TOOLING_TAG_KEY` and clear the local
variables holding the parsed records and keys. This procedure does not start
the proxy, contact Azure or authorize Record.

The fixture explicitly uses `AzureCliCredential` for Record and the shared mock
credential for Playback. The CLI must be logged into the approved tenant and
cloud, with the required read permissions. No credential/token is recorded in
test variables. The audience's trailing slash is normalized before constructing
`ArmEnvironment`, which appends `/.default`; the resulting token scope is
`https://management.azure.com/.default`. The proxy uses the package's existing `assets.json` workflow;
restore the repository-pinned proxy if missing before the approved run:

```powershell
dotnet tool restore --tool-manifest .\.config\dotnet-tools.json
```

This restores tools from the existing repository manifest without upgrading
versions or starting the proxy server. The recorded test starts the proxy when
executed. Do not upload assets during recording.

## Offline validation

This filter selects only mock-backed/unit tests, not the recorded fixture:

```powershell
$env:AZURE_TEST_MODE = 'Playback'
dotnet test .\sdk\storage\Azure.ResourceManager.Storage\tests\Azure.ResourceManager.Storage.Tests.csproj `
  -f net10.0 --filter 'FullyQualifiedName~StorageKeyCreationTimeUnitTests'
```

No proxy or Azure authentication is needed for those mock-backed tests.

## Recorded Playback

From the shared SDK branch, restore the repository-pinned tools and the exact
recording tag referenced by the package manifest, then run only the two net10.0
recorded cases:

```powershell
dotnet tool restore --tool-manifest .\.config\dotnet-tools.json
dotnet tool run test-proxy -- restore `
  -a .\sdk\storage\Azure.ResourceManager.Storage\assets.json

$env:AZURE_TEST_MODE = 'Playback'
$env:AZURE_DISABLE_AUTO_RECORDING = 'true'
$env:AZURE_DISABLE_BOOTSTRAPPING = 'true'
[Environment]::SetEnvironmentVariable(
  'STORAGE_KEY_CREATION_TIME_TOOLING_TAG_KEY', $null, 'Process')

dotnet test .\sdk\storage\Azure.ResourceManager.Storage\tests\Azure.ResourceManager.Storage.Tests.csproj `
  -f net10.0 `
  --filter 'FullyQualifiedName~StorageKeyCreationTimeRecordedTests&FullyQualifiedName~GetAndListsExposeKeyCreationTimestamps' `
  --logger 'console;verbosity=normal' `
  --logger 'trx;LogFileName=key-creation-time-playback.trx'
```

Require exactly two executed/passed cases, one sync and one async, with zero
failed/skipped. Exit code zero alone is insufficient. Neither Azure login nor
real target identifiers nor private tooling-tag input is needed: Playback uses
the mock credential and recorded target/routing variables. It selects the saved
September API version and preserves exact API-version matching and all positive
timestamp assertions. Automatic re-recording and bootstrapping remain disabled.
The framework starts a local proxy for Playback and stops it after the run;
verify no helper remains and that both recording hashes are unchanged.

Required tooling is Git, PowerShell 7+, a .NET 10 SDK allowed by `global.json`,
and the repository-pinned test proxy restored above. TypeSpec generation,
Node.js and Azure CLI authentication are not required for Playback.

## Recording review and sanitization

Record: **2 passed**. Playback: **2 passed**. Both runs executed exactly the
net10.0 sync and async variants, with zero failed/skipped. Playback required no
Azure credentials or private tooling-tag input and left both recordings
unchanged. This is December-generated SDK validation using explicitly approved
September requests; actual December endpoint validation remains deferred.

Each recording contains three
successful GET requests: account GET, RG LIST and subscription LIST, with
1/3/200 accounts respectively and the target matched exactly once per operation.
All six target timestamps agree across operations and variants, retaining
seven-digit fractional precision. Neither LIST returned a continuation.

Complete inspection verified two original-request response headers per file,
both redacted, the complete response bodies, preserved counts/structure and
timestamp text, and the absence of duplicate JSON keys or remaining known
identifier candidates. The sync bodies matched the in-memory prior baseline
except the approved redactions, and the async bodies matched the inspected sync
bodies. Live pagination was not exercised; forced continuation requests are
covered offline. Publishing requires separate owner approval.

Residual review classified the trace IDs as diagnostic correlation identifiers,
not credentials or resource identities. The fixture now also redacts
`x-ms-original-request-ids` using the standard header sanitizer. Protocol headers
such as content type, retry/challenge headers and Location are not removed.
The consolidated recordings now contain this redaction.

Full field-context review also identified staging-qualified region labels in
unrelated accounts' location/primaryLocation fields. These are internal
environment metadata, unlike the public base region. A targeted body-key rule
removes only a trailing `(stage)` qualifier (case-insensitive) from location,
primaryLocation and secondaryLocation strings. It retains the base region,
field types and account counts, and does not change ARM routing or service
configuration. No staging qualifier remains in either consolidated recording.

All three distinct observed tag keys were reviewed. The generic test and Azure
resource-usage labels remain distinct and unchanged. The owner's clarified
policy supersedes the earlier retention disposition: treat the tooling-prefixed
creator label as internal and rename only that exact privately supplied key.
Its replacement is `sanitized-tooling-created-by`; all tag values remain
redacted. A key absent from an initial scanner allowlist is not by itself
evidence that it is sensitive.

The standard body-regex sanitizer matches only the exact JSON-escaped property
name in `tags` dictionaries, not similar prefixes, ordinary properties or text.
GET and every account on every LIST page are checked before a successful
recording: the canonical key is reserved even in accounts without the source
key, spelling/case mismatches and duplicate/colliding tag names fail explicitly,
and at least one configured key must be observed across the complete scenario.
No key values are included in those failure messages. Canonical Playback input
is idempotent; Record rejects that placeholder as its private source input.
Synthetic tests verify dictionary counts, timestamp strings,
target consistency, both variants and paginated September requests. These
checks do not replace inspection of the actual proxy-saved JSON.

The earlier substring scan retained only aggregate counts, not per-match
provenance. Do not treat those counts as confirmed leaks. A complete current-file
field-context review distinguishes schema/enum text, placeholders, approved
timestamps and diagnostic correlation data; it does not reconstruct the exact
historical substring-to-field mapping.

The pinned framework does not expose a supported offline rewrite workflow:
adding Playback sanitizers does not sanitize saved assets. The approved
consolidated re-record has now persisted the additional header, region-label and
exact tooling tag-key redactions, followed by the successful local-only Playback
run described above. No further live retry is authorized by this guide;
publication requires separate approval.

The fixture uses the shared proxy's `BodyKeySanitizer`, `BodyRegexSanitizer`,
`UriRegexSanitizer`, JSON-path and header sanitizer registrations:

- Recorded variables replace the subscription with the empty GUID, the target
  RG with `target-rg`, and the target account with `targetaccount`. The same
  deterministic path substitutions apply to request URIs and JSON, including
  account IDs, nextLink paths, identity dictionary keys and resource references.
  Record uses the real local target; Playback uses only the saved replacements.
- Other RG/resource names use `sanitized`. These placeholders are intentionally
  many-to-one for unrelated resources; they are not evidence of unrelated
  resource uniqueness. All accounts, array elements and fields remain present.
  The target retains a distinct ID for the existing GET/LIST assertions.
- Tenant/principal/client/object/domain GUID fields use the empty GUID. Active
  Directory domain/forest names use `sanitized.example`, NetBIOS/SAM names use
  `sanitized`, and SID fields use a fixed synthetic SID.
- Network IP rule values use documentation addresses (`192.0.2.1` and
  `2001:db8::1`); virtual network/subnet/private endpoint references retain
  resource-ID structure with replaced names. Private-link descriptions are
  redacted. Key Vault names, versions and endpoint strings are replaced. Only
  the internal staging qualifier is stripped from region labels; ordinary
  public regions and the approved regional endpoint are retained.
- Primary/secondary endpoint string leaves and custom domains are sanitized
  without replacing nested endpoint objects. Other HTTPS endpoint hosts use
  `sanitized.example`; the approved regional ARM host and public ARM audience
  remain unchanged for routing, guards and exact request matching.
- Existing authentication/secret, tag-value and system-data identity sanitizers
  remain in use. Request/correlation/routing/original-request response IDs and
  ETags are redacted. Unrelated diagnostic correlation formats are reviewed in
  context rather than treating every nonzero identifier as a secret.

The fixed substitutions cannot preserve distinct dictionary keys if an account
has multiple user-assigned identities. Validation fails explicitly on that
shape before a successful recording can be saved; it does not remove identities
or silently merge their keys. Stop and obtain a distinct replacement plan if
this occurs. Synthetic offline tests cover the supported empty/single-key
shapes, rejection of multiple keys, references, response structure/account
counts, timestamp text/precision, target assertions and paged September requests.
These tests exercise configured rules locally, not the proxy recording engine.

Keep all timestamps and the actual `api-version` intact. Tag keys, unknown
properties, arbitrary free text, additional headers and opaque continuation
tokens still require full publication review; a generic sanitizer is not proof
that these are safe. Do not remove accounts or fields to conceal them. Review
both complete re-recorded variants and every LIST page, then separately approve
Playback and publication. Neither is authorized by sanitizer preparation.

Verify actual request versions and Canary routing evidence in Record results.
If a live LIST has one page, report that limitation; only the offline mock tests
prove forced continuation handling. Missing feature exposure or LIST permission
is a blocker, not grounds to weaken assertions. December endpoint validation
remains deferred until service deployment; remove the process-local version
override (or explicitly select `2026-12-01`) for that later, separately approved
run.
