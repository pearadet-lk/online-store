.PHONY: help deploy-minikube teardown-minikube deploy-docker-local port-forward-minikube stop-port-forward-minikube deploy-dockerdesktop-k8s teardown-dockerdesktop-k8s port-forward-dockerdesktop-k8s stop-port-forward-dockerdesktop-k8s

POWERSHELL ?= powershell

help:
	@echo Available targets:
	@echo "  make deploy-minikube      Build images and deploy to Minikube"
	@echo "  make teardown-minikube    Remove the online-store namespace from Minikube"
	@echo "  make deploy-docker-local  Start local Docker Compose environment"
	@echo "  make port-forward-minikube  Start all Minikube service port-forwards"
	@echo "  make stop-port-forward-minikube  Stop all Minikube service port-forwards"
	@echo "  make deploy-dockerdesktop-k8s  Build images and deploy to Docker Desktop Kubernetes"
	@echo "  make teardown-dockerdesktop-k8s  Remove namespace/manifests from Docker Desktop Kubernetes"
	@echo "  make port-forward-dockerdesktop-k8s  Start all Docker Desktop Kubernetes port-forwards"
	@echo "  make stop-port-forward-dockerdesktop-k8s  Stop all Docker Desktop Kubernetes port-forwards"

deploy-minikube:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File ./scripts/deploy-minikube.ps1

teardown-minikube:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File ./scripts/teardown-minikube.ps1

deploy-docker-local:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File ./scripts/deploy-docker-local.ps1

port-forward-minikube:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File ./scripts/port-forward-minikube.ps1

stop-port-forward-minikube:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File ./scripts/stop-port-forward-minikube.ps1

deploy-dockerdesktop-k8s:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File ./scripts/deploy-dockerdesktop-k8s.ps1

teardown-dockerdesktop-k8s:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File ./scripts/teardown-dockerdesktop-k8s.ps1

port-forward-dockerdesktop-k8s:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File ./scripts/port-forward-dockerdesktop-k8s.ps1

stop-port-forward-dockerdesktop-k8s:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File ./scripts/stop-port-forward-dockerdesktop-k8s.ps1
