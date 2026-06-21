# Cloudflare R2 Storage Provider

Deploys Addressables build output to a Cloudflare R2 bucket using the AWS CLI
(R2 exposes an S3-compatible API). No egress fees — ideal for game content distribution.

---

## Prerequisites

| Requirement | Notes |
|---|---|
| [AWS CLI v2](https://aws.amazon.com/cli/) | Used to talk to R2's S3-compatible API |
| Cloudflare account | Free tier includes 10 GB storage, 1 million Class B ops/month |
| R2 enabled | Cloudflare Dashboard → R2 → enable R2 on your account |

---

## One-Time Setup

### 1. Create an R2 Bucket

- Cloudflare Dashboard → R2 → **Create bucket**
- Name it (e.g. `molca-content`)
- Location: automatic (Cloudflare places it close to your origin)

### 2. Enable Public Access

- Open the bucket → **Settings** → **Public Access**
- For production: **Custom Domain** → add a domain you control (e.g. `cdn.yourdomain.com`)
- For testing: enable **R2.dev subdomain** (gives a `pub-<hash>.r2.dev` URL)

> Custom domain is recommended — the R2.dev URL is rate-limited and not suitable for production.

### 3. CORS Policy (WebGL or browser clients)

Bucket → Settings → **CORS Policy**:
```json
[
  {
    "AllowedOrigins": ["*"],
    "AllowedMethods": ["GET", "HEAD"],
    "AllowedHeaders": ["*"],
    "MaxAgeSeconds": 3600
  }
]
```

### 4. Create an R2 API Token

- Cloudflare Dashboard → R2 → **Manage R2 API Tokens** → Create API Token
- Permissions: **Object Read & Write** (scope to your bucket for least privilege)
- Copy the **Access Key ID** and **Secret Access Key** — shown only once

### 5. Find Your Account ID

Right sidebar of the Cloudflare Dashboard main page, or under R2 overview.

### 6. Configure AWS CLI with R2 Credentials

```sh
aws configure --profile cloudflare-r2

# AWS Access Key ID:     <R2 Access Key ID>
# AWS Secret Access Key: <R2 Secret Access Key>
# Default region:        auto
# Default output format: json
```

Verify the connection:
```sh
aws s3 ls s3://your-bucket-name \
  --endpoint-url https://<account-id>.r2.cloudflarestorage.com \
  --profile cloudflare-r2
```

---

## Unity Setup

### 1. Create the Provider Asset

**Assets > Create > Molca > Content Package > Storage > Cloudflare R2**

### 2. Configure Fields

| Field | Description |
|---|---|
| **Account ID** | Your Cloudflare Account ID (used to build the endpoint URL) |
| **Bucket Name** | R2 bucket name |
| **Key Prefix** | Folder inside bucket (e.g. `content/`) |
| **AWS Profile** | CLI profile configured with R2 credentials (default: `cloudflare-r2`) |
| **Dry Run** | Test what would upload without transferring |
| **Delete Removed** | Remove remote files no longer in local build output |
| **Extra Args** | Appended verbatim to the `aws s3 sync` command |

### 3. Assign to Build Config

Open `ContentPackageBuildConfig` → drag the provider asset into **Storage Provider**.

Set **Remote Load URL** to your public R2 URL:
```
# Custom domain (recommended)
https://cdn.yourdomain.com/content/[BuildTarget]

# R2.dev subdomain (testing only)
https://pub-<hash>.r2.dev/content/[BuildTarget]
```

---

## How the Endpoint URL is Built

The provider automatically constructs:
```
https://<accountId>.r2.cloudflarestorage.com
```
from the **Account ID** field. You do not need to enter it manually.

---

## CI/CD (GitHub Actions)

```yaml
env:
  AWS_ACCESS_KEY_ID: ${{ secrets.R2_ACCESS_KEY_ID }}
  AWS_SECRET_ACCESS_KEY: ${{ secrets.R2_SECRET_ACCESS_KEY }}
  AWS_DEFAULT_REGION: auto
  AWS_ENDPOINT_URL: https://${{ secrets.CF_ACCOUNT_ID }}.r2.cloudflarestorage.com
```

Leave **AWS Profile** empty in the provider asset — the CLI uses env vars.

---

## Pricing (as of 2025)

| Resource | Free tier | Paid |
|---|---|---|
| Storage | 10 GB / month | $0.015 / GB |
| Class A ops (write) | 1M / month | $4.50 / million |
| Class B ops (read) | 10M / month | $0.36 / million |
| **Egress** | **Free** | **Free** |

Zero egress cost is the main advantage over S3 + CloudFront for player downloads.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| `AWS CLI not found in PATH` | Install AWS CLI v2 and restart the Unity Editor |
| `Account ID is not set` | Fill in the Account ID field in the provider asset |
| `InvalidAccessKeyId` | Check R2 API token is correct and profile name matches |
| `NoSuchBucket` | Verify bucket name (R2 bucket names are case-sensitive) |
| Players can't download | Confirm Public Access is enabled and custom domain is active |
| `AuthorizationHeaderMalformed` | Ensure region is set to `auto` in your AWS CLI profile |
| Deploy succeeds but app loads old content | Call `RefreshCatalogAsync()` or enable **Check for Updates** in ContentPackageSettings |
