.PHONY: help deploy-minikube teardown-minikube deploy-docker-local

POWERSHELL ?= powershell

help:
	@echo Available targets:
	@echo "  make deploy-minikube      Build images and deploy to Minikube"
	@echo "  make teardown-minikube    Remove the online-store namespace from Minikube"
	@echo "  make deploy-docker-local  Start local Docker Compose environment"

deploy-minikube:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File ./scripts/deploy-minikube.ps1

teardown-minikube:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File ./scripts/teardown-minikube.ps1

deploy-docker-local:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File ./scripts/deploy-docker-local.ps1
