# Google Cloud Storage Provider

Deploys Addressables build output to a Google Cloud Storage (GCS) bucket
using the `gcloud storage rsync` command (Google Cloud SDK).

---

## Prerequisites

| Requirement | Notes |
|---|---|
| [Google Cloud SDK](https://cloud.google.com/sdk/docs/install) | Install and add to PATH, then restart Unity Editor |
| Google Cloud account | Billing must be enabled on the project |
| GCS bucket | Created in the GCS Console or via `gcloud` |

---

## One-Time Setup

### 1. Install Google Cloud SDK

Download from https://cloud.google.com/sdk/docs/install and run the installer.
Verify installation:
```sh
gcloud --version
```

### 2. Authenticate

```sh
# Interactive login (for local development)
gcloud auth login

# For deployment scripts / CI: use a service account key instead (see CI/CD section)
```

### 3. Set a Default Project

```sh
gcloud config set project your-gcp-project-id
```

### 4. Create a GCS Bucket

```sh
gcloud storage buckets create gs://your-bucket-name \
  --location=asia-southeast1 \
  --uniform-bucket-level-access
```

Recommended regions for Indonesia:
- `asia-southeast1` — Singapore (closest with GA SLA)
- `asia-southeast2` — Jakarta (available, check current status)

### 5. Enable Public Access (if players download directly)

```sh
gcloud storage buckets add-iam-policy-binding gs://your-bucket-name \
  --member=allUsers \
  --role=roles/storage.objectViewer
```

Or in the GCS Console: bucket → **Permissions** → Grant `allUsers` the `Storage Object Viewer` role.

> Note: Uniform bucket-level access must be enabled (set in step 4) to use this IAM binding.

### 6. Set Up Cloud CDN (optional but recommended)

GCS can be fronted by Cloud CDN via a Load Balancer:

- GCS Console → bucket → **Edit website configuration** (for static hosting)
- Or: GCP Console → Network Services → **Cloud CDN** → set backend to your bucket

Use the CDN URL as `remoteLoadURL` in `ContentPackageBuildConfig`.

---

## Unity Setup

### 1. Create the Provider Asset

**Assets > Create > Molca > Content Package > Storage > Google Cloud Storage**

### 2. Configure Fields

| Field | Description |
|---|---|
| **GCS Bucket** | Bucket name (without `gs://`) |
| **GCS Key Prefix** | Folder inside bucket (e.g. `content/`) |
| **Gcloud Configuration** | Named `gcloud` configuration to use. Empty = active configuration |
| **Impersonate Service Account** | Optional service account email for impersonation |
| **Dry Run** | Test what would sync without transferring (`--dry-run`) |
| **Delete Removed** | Remove remote files no longer in local build output (`-d`) |
| **Extra Args** | Appended verbatim to `gcloud storage rsync` |

### 3. Assign to Build Config

Open `ContentPackageBuildConfig` → drag the provider asset into **Storage Provider**.

Set **Remote Load URL** to your GCS public URL:
```
# Direct GCS URL
https://storage.googleapis.com/your-bucket-name/content/[BuildTarget]

# Cloud CDN custom domain
https://cdn.yourdomain.com/content/[BuildTarget]
```

---

## Named Configurations (multiple environments)

Use named `gcloud` configurations to switch between staging and production:

```sh
# Create staging config
gcloud config configurations create molca-staging
gcloud config set project molca-staging-project
gcloud auth login

# Create production config
gcloud config configurations create molca-prod
gcloud config set project molca-prod-project
gcloud auth login
```

Set **Gcloud Configuration** in the provider asset to `molca-staging` or `molca-prod`.

---

## CI/CD (GitHub Actions)

Use a service account key stored as a secret:

```yaml
- name: Authenticate to GCP
  uses: google-github-actions/auth@v2
  with:
    credentials_json: ${{ secrets.GCP_SERVICE_ACCOUNT_KEY }}

- name: Set up Cloud SDK
  uses: google-github-actions/setup-gcloud@v2

- name: Deploy
  run: gcloud storage rsync "ServerData/StandaloneWindows64" gs://your-bucket/content/StandaloneWindows64/ -r
```

Leave **Gcloud Configuration** empty in the provider asset — the action sets up credentials via env vars.

---

## CORS (WebGL or browser clients)

Create a `cors.json` file:
```json
[
  {
    "origin": ["*"],
    "method": ["GET", "HEAD"],
    "responseHeader": ["Content-Type"],
    "maxAgeSeconds": 3600
  }
]
```

Apply it:
```sh
gcloud storage buckets update gs://your-bucket-name --cors-file=cors.json
```

---

## Pricing (as of 2025)

| Resource | Cost |
|---|---|
| Storage | $0.020 / GB / month (Standard, asia-southeast1) |
| Egress to internet | $0.12 / GB (first 1 TB/month) |
| Class A ops (write) | $0.05 / 10,000 |
| Class B ops (read) | $0.004 / 10,000 |

> Egress is charged unlike Cloudflare R2. For high download volume, consider fronting with Cloud CDN which reduces egress cost via caching.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| `gcloud CLI not found in PATH` | Install Google Cloud SDK and restart the Unity Editor |
| `Access denied` | Check bucket IAM permissions and authenticated account |
| `BucketNotFoundException` | Verify bucket name (GCS names are globally unique and case-sensitive) |
| Players can't download | Confirm `allUsers` has `Storage Object Viewer` role |
| `gcloud storage` command not found | Update SDK: `gcloud components update` |
| Deploy succeeds but app loads old content | Call `RefreshCatalogAsync()` or enable **Check for Updates** in ContentPackageSettings |
