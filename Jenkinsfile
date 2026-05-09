pipeline {
  agent any

  options {
    timestamps()
    ansiColor('xterm')
  }

  parameters {
    choice(name: 'CLOUD', choices: ['eks', 'aks', 'gke'], description: 'Target cloud')
    string(name: 'IMAGE_TAG', defaultValue: '', description: 'Image tag (defaults to commit SHA)')
  }

  environment {
    NAMESPACE = 'online-store'
  }

  stages {
    stage('Checkout') {
      steps {
        checkout scm
      }
    }

    stage('Build .NET') {
      steps {
        sh 'dotnet restore OnlineStore.sln'
        sh 'dotnet build OnlineStore.sln --configuration Release --no-restore'
        sh 'dotnet test tests/Gateway.Tests/Gateway.Tests.csproj --configuration Release --no-build'
        sh 'dotnet test tests/EndToEnd.Tests/EndToEnd.Tests.csproj --configuration Release --no-build'
      }
    }

    stage('Resolve Target') {
      steps {
        script {
          env.TAG = params.IMAGE_TAG?.trim() ? params.IMAGE_TAG.trim() : sh(script: 'git rev-parse --short=12 HEAD', returnStdout: true).trim()
          env.OVERLAY = "k8s/production/overlays/${params.CLOUD}"

          if (params.CLOUD == 'eks') {
            env.REGISTRY = env.ECR_REGISTRY
          } else if (params.CLOUD == 'aks') {
            env.REGISTRY = env.ACR_LOGIN_SERVER
          } else {
            env.REGISTRY = env.GAR_REPOSITORY_PREFIX
          }

          env.BUILD_ALL = "false"
          env.CHANGED_GATEWAY = "false"
          env.CHANGED_ORDER = "false"
          env.CHANGED_PAYMENT = "false"
          env.CHANGED_PRODUCT = "false"
          env.CHANGED_CART = "false"
          env.CHANGED_USER = "false"
          env.CHANGED_INVENTORY = "false"
          env.CHANGED_SHIPPING = "false"
          env.CHANGED_HISTORY = "false"

          def hasPrevCommit = sh(script: 'git rev-parse --verify HEAD~1 >/dev/null 2>&1', returnStatus: true) == 0
          if (!hasPrevCommit) {
            env.BUILD_ALL = "true"
            return
          }

          def changedFiles = sh(script: 'git diff --name-only HEAD~1 HEAD', returnStdout: true).trim()
          if (!changedFiles) {
            return
          }

          def changedList = changedFiles.split('\n') as List<String>
          def requiresFullBuild = changedList.any { f ->
            f.startsWith('src/BuildingBlocks/') ||
            f == 'Directory.Build.props' ||
            f == 'Directory.Packages.props' ||
            f == 'NuGet.config' ||
            f == 'docker-compose.yml' ||
            f.startsWith('k8s/') ||
            f.startsWith('.github/workflows/')
          }

          if (requiresFullBuild) {
            env.BUILD_ALL = "true"
            return
          }

          env.CHANGED_GATEWAY = changedList.any { it.startsWith('src/Services/Gateway/') } ? "true" : "false"
          env.CHANGED_ORDER = changedList.any { it.startsWith('src/Services/OrderService/') } ? "true" : "false"
          env.CHANGED_PAYMENT = changedList.any { it.startsWith('src/Services/PaymentService/') } ? "true" : "false"
          env.CHANGED_PRODUCT = changedList.any { it.startsWith('src/Services/ProductService/') } ? "true" : "false"
          env.CHANGED_CART = changedList.any { it.startsWith('src/Services/CartService/') } ? "true" : "false"
          env.CHANGED_USER = changedList.any { it.startsWith('src/Services/UserService/') } ? "true" : "false"
          env.CHANGED_INVENTORY = changedList.any { it.startsWith('src/Services/InventoryService/') } ? "true" : "false"
          env.CHANGED_SHIPPING = changedList.any { it.startsWith('src/Services/ShippingService/') } ? "true" : "false"
          env.CHANGED_HISTORY = changedList.any { it.startsWith('src/Services/HistoryService/') } ? "true" : "false"
        }
      }
    }

    stage('Registry Login') {
      steps {
        script {
          if (params.CLOUD == 'eks') {
            sh '''
              aws ecr get-login-password --region "$AWS_REGION" | docker login --username AWS --password-stdin "$ECR_REGISTRY"
            '''
          } else if (params.CLOUD == 'aks') {
            sh '''
              az acr login --name "$ACR_NAME"
            '''
          } else {
            sh '''
              gcloud auth configure-docker "$GCP_ARTIFACT_REGISTRY_HOST" --quiet
            '''
          }
        }
      }
    }

    stage('Build and Push Images') {
      steps {
        sh '''
          set -eu
          should_build() {
            local changed="$1"
            if [ "$BUILD_ALL" = "true" ] || [ "$changed" = "true" ]; then
              echo "true"
            else
              echo "false"
            fi
          }

          build_service() {
            local name="$1"
            local dockerfile="$2"
            local changed="$3"
            if [ "$(should_build "$changed")" != "true" ]; then
              echo "Skipping ${name} (unchanged)"
              return
            fi
            image="${REGISTRY}/online-store-${name}:${TAG}"
            echo "Building ${image}"
            docker build -f "${dockerfile}" -t "${image}" .
            docker push "${image}"
          }

          build_service "gateway" "src/Services/Gateway/Dockerfile" "$CHANGED_GATEWAY"
          build_service "order-service" "src/Services/OrderService/Dockerfile" "$CHANGED_ORDER"
          build_service "payment-service" "src/Services/PaymentService/Dockerfile" "$CHANGED_PAYMENT"
          build_service "product-service" "src/Services/ProductService/Dockerfile" "$CHANGED_PRODUCT"
          build_service "cart-service" "src/Services/CartService/Dockerfile" "$CHANGED_CART"
          build_service "user-service" "src/Services/UserService/Dockerfile" "$CHANGED_USER"
          build_service "inventory-service" "src/Services/InventoryService/Dockerfile" "$CHANGED_INVENTORY"
          build_service "shipping-service" "src/Services/ShippingService/Dockerfile" "$CHANGED_SHIPPING"
          build_service "history-service" "src/Services/HistoryService/Dockerfile" "$CHANGED_HISTORY"
        '''
      }
    }

    stage('Configure Cluster Access') {
      steps {
        script {
          if (params.CLOUD == 'eks') {
            sh 'aws eks update-kubeconfig --name "$EKS_CLUSTER_NAME" --region "$AWS_REGION"'
          } else if (params.CLOUD == 'aks') {
            sh 'az aks get-credentials --resource-group "$AKS_RESOURCE_GROUP" --name "$AKS_CLUSTER_NAME" --overwrite-existing'
          } else {
            sh 'gcloud container clusters get-credentials "$GKE_CLUSTER_NAME" --region "$GKE_CLUSTER_REGION" --project "$GCP_PROJECT_ID"'
          }
        }
      }
    }

    stage('Deploy') {
      steps {
        sh 'kubectl apply -k "$OVERLAY"'
        sh '''
          should_update() {
            local changed="$1"
            if [ "$BUILD_ALL" = "true" ] || [ "$changed" = "true" ]; then
              echo "true"
            else
              echo "false"
            fi
          }

          update_image() {
            local deployment="$1"
            local container="$2"
            local image="$3"
            local changed="$4"
            if [ "$(should_update "$changed")" != "true" ]; then
              echo "No image update for ${deployment}"
              return
            fi
            kubectl -n "$NAMESPACE" set image "deployment/${deployment}" "${container}=${REGISTRY}/${image}:${TAG}"
          }

          update_image "gateway" "gateway" "online-store-gateway" "$CHANGED_GATEWAY"
          update_image "order-service" "order-service" "online-store-order-service" "$CHANGED_ORDER"
          update_image "payment-service" "payment-service" "online-store-payment-service" "$CHANGED_PAYMENT"
          update_image "product-service" "product-service" "online-store-product-service" "$CHANGED_PRODUCT"
          update_image "cart-service" "cart-service" "online-store-cart-service" "$CHANGED_CART"
          update_image "user-service" "user-service" "online-store-user-service" "$CHANGED_USER"
          update_image "inventory-service" "inventory-service" "online-store-inventory-service" "$CHANGED_INVENTORY"
          update_image "shipping-service" "shipping-service" "online-store-shipping-service" "$CHANGED_SHIPPING"
          update_image "history-service" "history-service" "online-store-history-service" "$CHANGED_HISTORY"
          kubectl -n "$NAMESPACE" rollout status deployment/gateway --timeout=300s
          kubectl -n "$NAMESPACE" port-forward svc/gateway 18080:8080 >/tmp/gateway-port-forward.log 2>&1 &
          PF_PID=$!
          trap "kill $PF_PID" EXIT
          sleep 5
          curl -fsS "http://127.0.0.1:18080/health"
          curl -fsS "http://127.0.0.1:18080/api/docs"
          PRODUCTS_STATUS=$(curl -s -o /dev/null -w "%{http_code}" "http://127.0.0.1:18080/api/products")
          if [ "$PRODUCTS_STATUS" != "401" ]; then
            echo "Expected /api/products to return 401 without JWT, got $PRODUCTS_STATUS"
            exit 1
          fi
          LOGIN_RESPONSE=$(curl -fsS -X POST "http://127.0.0.1:18080/api/users/login" \
            -H "Content-Type: application/json" \
            -d '{"email":"demo@example.com","password":"demo-password"}')
          ACCESS_TOKEN=$(python3 -c 'import json,sys;print(json.loads(sys.stdin.read()).get("accessToken",""))' <<<"$LOGIN_RESPONSE")
          if [ -z "$ACCESS_TOKEN" ]; then
            echo "Failed to obtain access token from gateway login response."
            exit 1
          fi
          AUTH_PRODUCTS_STATUS=$(curl -s -o /dev/null -w "%{http_code}" \
            -H "Authorization: Bearer $ACCESS_TOKEN" \
            "http://127.0.0.1:18080/api/products")
          if [ "$AUTH_PRODUCTS_STATUS" != "200" ]; then
            echo "Expected /api/products to return 200 with JWT, got $AUTH_PRODUCTS_STATUS"
            exit 1
          fi
          kill $PF_PID
        '''
      }
    }
  }
}
