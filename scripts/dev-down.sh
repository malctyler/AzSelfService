#!/bin/bash

echo "🛑 Stopping AzSelfService Development Environment"
echo "=================================================="
echo ""

# Stop and remove containers, keep volumes for data persistence
docker-compose --profile dev down --remove-orphans

echo ""
echo "✓ Services stopped"
echo ""
echo "💾 Volumes preserved (data persisted):"
echo "   - postgres_data: PostgreSQL database"
echo "   - terraform_tmp: Terraform working directory"
echo ""
echo "🧹 To completely clean up (remove volumes):"
echo "   docker-compose --profile dev down -v"
echo ""
echo "🚀 To restart: ./scripts/dev-up.sh"
echo ""
