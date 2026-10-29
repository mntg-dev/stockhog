# Release distribution

Pushing a `v*` tag runs [release.yml](../.github/workflows/release.yml). That workflow:

1. builds the self-contained app, the `.zip` and the `.msi`, each with a `.sha256` file;
2. creates a GitHub release with those files attached;
3. copies the same files to the Azure Storage account `emtradingstorage`, container `releases`:

```
releases/
  v1.2.0/Stockhog-v1.2.0-win-x64.msi (+ .sha256)   never overwritten, cached for a year
  v1.2.0/Stockhog-v1.2.0-win-x64.zip (+ .sha256)
  latest/Stockhog-Setup.msi                        replaced on every release, cached 5 min
  latest.json                                       manifest read by the landing page
```

Links to share:

- Latest installer: `https://emtradingstorage.blob.core.windows.net/releases/latest/Stockhog-Setup.msi`
- A specific version: `https://emtradingstorage.blob.core.windows.net/releases/v1.2.0/Stockhog-v1.2.0-win-x64.msi`

The workflow logs in to Azure with GitHub OIDC, so it stores no account key or SAS token. The steps below are the one-time setup that makes this work.

---

## One-time setup

Run the commands in Git Bash with the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) and the [GitHub CLI](https://cli.github.com/), after running `az login` and `gh auth login`.

### 0. Set variables

```bash
# Git Bash rewrites arguments that start with "/" into Windows paths, which breaks --scope "/subscriptions/...".
export MSYS_NO_PATHCONV=1

RG="<resource-group-of-emtradingstorage>"   # az storage account show -n emtradingstorage --query resourceGroup -o tsv
ACCOUNT="emtradingstorage"
CONTAINER="releases"
REPO="mntg-dev/stockhog"
SUB_ID=$(az account show --query id -o tsv)
TENANT_ID=$(az account show --query tenantId -o tsv)
```

### 1. Commit the installer project

The release build needs these files, and they aren't in git yet:

```bash
git add application/installer/Stockhog.Installer/Stockhog.Installer.wixproj \
        application/installer/Stockhog.Installer/Package.wxs \
        application/installer/Stockhog.Installer/License.rtf \
        application/Stockhog.App/Assets/
git commit -m "wix installer project"
```

### 2. Create the public `releases` container

`--public-access blob` lets anyone download a file whose URL they know, but doesn't let them list the container.

```bash
az storage account update -n "$ACCOUNT" -g "$RG" --allow-blob-public-access true

# Lets your own account create the container and test uploads with --auth-mode login.
az role assignment create --assignee "$(az ad signed-in-user show --query id -o tsv)" \
  --role "Storage Blob Data Contributor" \
  --scope "/subscriptions/$SUB_ID/resourceGroups/$RG/providers/Microsoft.Storage/storageAccounts/$ACCOUNT"

az storage container create --account-name "$ACCOUNT" -n "$CONTAINER" \
  --public-access blob --auth-mode login
```

Role assignments can take a few minutes to apply. If you get `AuthorizationPermissionMismatch`, wait and try again.

### 3. Allow the landing page to read `latest.json` (CORS)

```bash
SWA_HOST=$(az staticwebapp show -n emtrading-landing-page --query defaultHostname -o tsv)
echo "$SWA_HOST"   # gray-plant-03997c40f.2.azurestaticapps.net

az storage cors add --account-name "$ACCOUNT" --services b \
  --methods GET HEAD --origins "https://$SWA_HOST" "http://localhost:5173" \
  --allowed-headers "*" --exposed-headers "*" --max-age 3600
```

If you add a custom domain to the landing page later, add it to `--origins` too. Without CORS the page still works: it falls back to the GitHub API.

### 4. Create the identity GitHub Actions logs in as

```bash
APP_ID=$(az ad app create --display-name "emtrading-release" --query appId -o tsv)
az ad sp create --id "$APP_ID"
```

### 5. Trust the GitHub `release` environment (federated credential)

Microsoft Entra federated credentials match the token subject exactly. A wildcard like `refs/tags/v*` doesn't work, so the trust is tied to a GitHub environment instead.

This repo's tokens put the numeric owner and repo IDs in the subject, e.g. `repo:mntg-dev@24610604/stockhog@1340081797:environment:release`, not just `repo:mntg-dev/stockhog:...`. So the subject is built from those IDs:

```bash
SUBJECT=$(gh api "repos/$REPO" --jq '"repo:\(.owner.login)@\(.owner.id)/\(.name)@\(.id):environment:release"')
echo "$SUBJECT"

az ad app federated-credential create --id "$APP_ID" --parameters "{
  \"name\": \"github-release-environment\",
  \"issuer\": \"https://token.actions.githubusercontent.com\",
  \"subject\": \"$SUBJECT\",
  \"audiences\": [\"api://AzureADTokenExchange\"]
}"
```

If login still fails with `AADSTS700213`, the error message quotes the exact subject GitHub sent. Put that string in the credential.

### 6. Give the identity write access to the `releases` container only

```bash
az role assignment create --assignee "$APP_ID" \
  --role "Storage Blob Data Contributor" \
  --scope "/subscriptions/$SUB_ID/resourceGroups/$RG/providers/Microsoft.Storage/storageAccounts/$ACCOUNT/blobServices/default/containers/$CONTAINER"
```

### 7. Create the GitHub environment and variables

```bash
gh api -X PUT "repos/$REPO/environments/release" --silent

gh variable set AZURE_CLIENT_ID       --env release --repo "$REPO" --body "$APP_ID"
gh variable set AZURE_TENANT_ID       --env release --repo "$REPO" --body "$TENANT_ID"
gh variable set AZURE_SUBSCRIPTION_ID --env release --repo "$REPO" --body "$SUB_ID"
```

None of these are secrets: without the federated credential they're useless.

Recommended: limit the environment to release tags. In GitHub, go to **Settings → Environments → release → Deployment branches and tags**, choose **Selected branches and tags**, and add the tag rule `v*`. After that, a workflow run on another branch can't get Azure access.

### 8. Publish a release

```bash
git tag v0.1.0
git push origin v0.1.0
```

Then check that everything was published:

```bash
curl -s https://emtradingstorage.blob.core.windows.net/releases/latest.json
curl -sI https://emtradingstorage.blob.core.windows.net/releases/latest/Stockhog-Setup.msi | head -5
```

Finally, open the landing page in a private window. The download button should show the new version and link to the `.msi`.

### 9. Retire the old `$web` link

Done: `$web/EmTrading-Setup.msi` (from before the app was renamed to Stockhog) has been removed and `$web` is empty. Use the `releases/latest/` link instead.

Optional: the storage account's static-website feature is still turned on, but with `$web` empty it serves nothing. To turn it off: `az storage blob service-properties update --account-name "$ACCOUNT" --static-website false`.

---

## Troubleshooting

| Error | Cause |
|---|---|
| `MissingSubscription` from `az role assignment create` (setup) | Git Bash turned `/subscriptions/...` into a Windows path. Run `export MSYS_NO_PATHCONV=1` (step 0). Also check that `$SUB_ID` and `$RG` aren't empty: variables don't carry over to a new terminal. |
| `AADSTS700213` / `AADSTS70021: No matching federated identity record` | The subject doesn't match. The job must run with `environment: release`, and the credential subject must equal the one quoted in the error, including the `@<id>` parts (step 5). |
| `AuthorizationPermissionMismatch` | The role from step 6 is missing, or hasn't applied yet. |
| `BlobAlreadyExists` | That tag was already uploaded. Versioned files are write-once on purpose. Publish a new tag, or delete the `vX.Y.Z/` blobs by hand if you really mean to replace them. |
| `PublicAccessNotPermitted` when downloading | Step 2 wasn't done: the account doesn't allow anonymous blob access. |
| Landing page shows the GitHub version, not the Azure one | CORS (step 3) is missing for that origin. Check the browser console. |

The GitHub release is created before the Azure upload. If the upload fails, fix the cause and copy the files from that GitHub release by hand. Rerunning the workflow would fail at `gh release create`, because that release already exists.

---

## Optional next steps

### Code signing (removes the SmartScreen warning)

At the moment Windows shows "Windows protected your PC" on first launch. To fix that:

1. Get a signing identity. Either:
   - **Azure Trusted Signing** (about $10/month; works from GitHub Actions without a key file). Check whether you're eligible as an individual in your country first.
   - an **OV code-signing certificate**, for example Certum's open-source developer certificate.
2. In `release.yml`, sign `dist/Stockhog.App.exe` and `dist/Stockhog.EngineRunner.exe` after publishing and before the WiX step. Then sign the `.msi` after it's built, before the hashes are computed.
3. Once signed builds ship, remove `smartScreenNote` from the landing page.

SmartScreen trust for a new certificate builds up as more people download the app; it isn't instant.

### winget

With stable versioned URLs, the app can go to the Windows Package Manager. Submit the first version with `wingetcreate new <versioned-msi-url>`. After that, add a `wingetcreate update` step to the workflow, using a GitHub PAT stored as a secret.

### Storage cleanup

To keep only recent versions, add a lifecycle management rule on the `releases` container that deletes blobs under `v*/` older than N days. Leave `latest/` and `latest.json` out of the rule.
