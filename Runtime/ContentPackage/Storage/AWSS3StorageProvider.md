# AWS S3 Storage Provider

Deploys Addressables build output to an Amazon S3 bucket using the AWS CLI.

---

## Prerequisites

| Requirement | Notes |
|---|---|
| [AWS CLI v2](https://aws.amazon.com/cli/) | Install and add to PATH, then restart Unity Editor |
| AWS account | IAM user or role with S3 write permissions |
| S3 bucket | Created in the AWS Console |

---

## One-Time Setup

### 1. Create an S3 Bucket

- AWS Console → S3 → **Create bucket**
- Choose a region close to your players (e.g. `ap-southeast-3` for Jakarta, `ap-southeast-1` for Singapore)
- Block public access: **off** if players download directly without authentication

### 2. Configure Public Access (if players download directly)

Bucket → Permissions → **Block public access** → disable all four toggles.

Add a bucket policy:
```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Principal": "*",
    "Action": "s3:GetObject",
    "Resource": "arn:aws:s3:::your-bucket/content/*"
  }]
}
```

### 3. Create an IAM User for Deployment

- AWS Console → IAM → Users → **Create user**
- Attach policy: `AmazonS3FullAccess` (or a scoped policy limited to your bucket)
- Create **Access Key** under Security credentials
- Save the Access Key ID and Secret Access Key

### 4. Configure AWS CLI

```sh
aws configure
# or for a named profile:
aws configure --profile molca-prod

# AWS Access Key ID:     <your access key>
# AWS Secret Access Key: <your secret key>
# Default region:        ap-southeast-1
# Default output format: json
```

### 5. Set Up CloudFront (optional but recommended)

Using CloudFront in front of S3 improves download speed and reduces S3 egress costs.

- AWS Console → CloudFront → **Create distribution**
- Origin: your S3 bucket
- Set the CloudFront domain as `remoteLoadURL` in `ContentPackageBuildConfig`

---

## Unity Setup

### 1. Create the Provider Asset

**Assets > Create > Molca > Content Package > Storage > AWS S3**

### 2. Configure Fields

| Field | Description |
|---|---|
| **S3 Bucket** | Bucket name (without `s3://`) |
| **S3 Region** | AWS region (e.g. `ap-southeast-1`) |
| **Key Prefix** | Folder inside bucket (e.g. `content/`) |
| **AWS Profile** | CLI profile name. Empty = default profile or env vars |
| **Dry Run** | Test what would upload without transferring |
| **Delete Removed** | Remove remote files no longer in local build output |
| **Extra Args** | Appended verbatim to `aws s3 sync` (e.g. `--acl public-read`) |

### 3. Assign to Build Config

Open `ContentPackageBuildConfig` → drag the provider asset into **Storage Provider**.

Set **Remote Load URL** to your S3 (or CloudFront) public URL:
```
https://your-bucket.s3.ap-southeast-1.amazonaws.com/content/[BuildTarget]
# or with CloudFront:
https://d1234abcd.cloudfront.net/content/[BuildTarget]
```

---

## CI/CD (GitHub Actions)

Store credentials as repository secrets, not in the provider asset:

```yaml
env:
  AWS_ACCESS_KEY_ID: ${{ secrets.AWS_ACCESS_KEY_ID }}
  AWS_SECRET_ACCESS_KEY: ${{ secrets.AWS_SECRET_ACCESS_KEY }}
  AWS_DEFAULT_REGION: ap-southeast-1
```

Leave **AWS Profile** empty — the CLI picks up env vars automatically.

---

## CORS (WebGL only)

Bucket → Permissions → **CORS configuration**:
```json
[
  {
    "AllowedHeaders": ["*"],
    "AllowedMethods": ["GET", "HEAD"],
    "AllowedOrigins": ["*"],
    "MaxAgeSeconds": 3600
  }
]
```

---

## Troubleshooting

| Problem | Fix |
|---|---|
| `AWS CLI not found in PATH` | Install AWS CLI v2 and restart the Unity Editor |
| `Access Denied` | Check IAM permissions and bucket policy |
| `NoSuchBucket` | Verify bucket name and region match |
| Players can't download | Check bucket public access and bucket policy |
| Deploy succeeds but app loads old content | Call `RefreshCatalogAsync()` or enable **Check for Updates** in ContentPackageSettings |
