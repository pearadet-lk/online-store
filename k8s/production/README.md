# Production Kubernetes Starter

This folder provides a production-ready Kubernetes starter using Kustomize:

- `base`: shared manifests for all clouds
- `overlays/eks`: AWS EKS-specific ingress annotations and host patch
- `overlays/aks`: Azure AKS-specific ingress annotations and host patch
- `overlays/gke`: Google GKE-specific ingress annotations and host patch

## Folder Layout

```text
k8s/production/
  base/
  overlays/
    eks/
    aks/
    gke/
```

## Important

- All secret values are intentionally empty placeholders.
- Update image registry names and tags before deploy.
- Replace ingress host and TLS annotations with your real domain/certificate details.
- `base/serviceaccount.yaml` sets namespace default service account with `imagePullSecrets`:
  - secret name: `registry-creds`
  - create this secret in each cluster before rollout

## Registry Pull Secret

Create Docker registry secret named `registry-creds`:

```powershell
kubectl -n online-store create secret docker-registry registry-creds `
  --docker-server=<registry-server> `
  --docker-username=<username> `
  --docker-password=<password> `
  --docker-email=<email>
```

## Workload Identity Patches

Each overlay patches the default service account with cloud-native identity annotation.
Values are intentionally empty and must be set:

- EKS: `k8s/production/overlays/eks/patch-serviceaccount.yaml`
  - `eks.amazonaws.com/role-arn: ""`
- AKS: `k8s/production/overlays/aks/patch-serviceaccount.yaml`
  - `azure.workload.identity/client-id: ""`
- GKE: `k8s/production/overlays/gke/patch-serviceaccount.yaml`
  - `iam.gke.io/gcp-service-account: ""`

## Deploy

```powershell
kubectl apply -k k8s/production/overlays/eks
kubectl apply -k k8s/production/overlays/aks
kubectl apply -k k8s/production/overlays/gke
```

Use only one overlay per target cluster.

## CI/CD Starters Included

- GitHub Actions multi-cloud deploy: `.github/workflows/deploy-k8s-multicloud.yml`
  - branch deploy mapping:
    - `deploy/eks` -> EKS
    - `deploy/aks` -> AKS
    - `deploy/gke` -> GKE
  - tag deploy mapping:
    - `eks-v*` -> EKS
    - `aks-v*` -> AKS
    - `gke-v*` -> GKE
- Jenkins pipeline: `Jenkinsfile` (parameterized `CLOUD=eks|aks|gke`)
- Azure DevOps starter: `azure-pipelines.yml` (AKS baseline)
