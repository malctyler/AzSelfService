.PHONY: help setup up down logs logs-api logs-postgres logs-frontend logs-worker status shell-db shell-api shell-frontend clean restart

help:
	@echo "AzSelfService Development Commands"
	@echo "==================================="
	@echo ""
	@echo "Setup & Cleanup:"
	@echo "  make setup          Setup development environment (.env.docker, directories)"
	@echo "  make up             Start all development containers"
	@echo "  make down           Stop all development containers"
	@echo "  make restart        Restart all services"
	@echo "  make clean          Remove containers and volumes (⚠️ loses database)"
	@echo ""
	@echo "Monitoring:"
	@echo "  make status         Show container status"
	@echo "  make logs           View logs from all services"
	@echo "  make logs-api       View backend API logs"
	@echo "  make logs-db        View PostgreSQL logs"
	@echo "  make logs-frontend  View frontend logs"
	@echo "  make logs-worker    View worker logs"
	@echo ""
	@echo "Shell Access:"
	@echo "  make shell-db       Open PostgreSQL shell"
	@echo "  make shell-api      Open backend container shell"
	@echo "  make shell-frontend Open frontend container shell"
	@echo ""
	@echo "Access Points:"
	@echo "  Frontend:  http://localhost:3000"
	@echo "  API:       http://localhost:5000"
	@echo "  API Docs:  http://localhost:5000/swagger"
	@echo "  DB:        localhost:5432"
	@echo ""
	@echo "Test Credentials:"
	@echo "  Username: admin"
	@echo "  Password: Test@1234"
	@echo ""

setup:
	@echo "🔧 Setting up development environment..."
	@bash scripts/dev-setup.sh

up:
	@echo "🚀 Starting services..."
	@bash scripts/dev-up.sh

down:
	@echo "🛑 Stopping services..."
	@bash scripts/dev-down.sh

restart: down up
	@echo "✓ Services restarted"

status:
	@docker-compose --profile dev ps

logs:
	@docker-compose --profile dev logs -f

logs-api:
	@docker-compose --profile dev logs -f backend

logs-db:
	@docker-compose --profile dev logs -f postgres

logs-frontend:
	@docker-compose --profile dev logs -f frontend

logs-worker:
	@docker-compose --profile dev logs -f worker

shell-db:
	@docker-compose exec postgres psql -U postgres -d azselfservice

shell-api:
	@docker-compose exec backend bash

shell-frontend:
	@docker-compose exec frontend sh

clean:
	@echo "🧹 Cleaning up containers and volumes..."
	@docker-compose --profile dev down -v
	@echo "✓ Cleanup complete (database data removed)"

.DEFAULT_GOAL := help
