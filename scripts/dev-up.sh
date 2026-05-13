#!/bin/bash
set -e

echo "🚀 Starting AzSelfService Development Environment"
echo "=================================================="
echo ""

# Load environment variables from the local file.
ENV_FILE=".env"
if [ ! -f "$ENV_FILE" ]; then
    if [ -f .env.docker ]; then
        ENV_FILE=".env.docker"
    else
        echo "❌ Error: neither .env nor .env.docker was found"
        echo "   Run: ./scripts/dev-setup.sh"
        exit 1
    fi
fi

set -a
. "./$ENV_FILE"
set +a

echo "✓ Loaded environment from $ENV_FILE"

HAS_REAL_AZURE_CREDS=true
if [ -z "${AZURE_CLIENT_ID:-}" ] || [ -z "${AZURE_TENANT_ID:-}" ] || [ -z "${AZURE_CLIENT_SECRET:-}" ] || \
    [ "${AZURE_CLIENT_ID}" = "00000000-0000-0000-0000-000000000000" ] || \
    [ "${AZURE_TENANT_ID}" = "00000000-0000-0000-0000-000000000000" ]; then
     HAS_REAL_AZURE_CREDS=false
fi

if [ "$HAS_REAL_AZURE_CREDS" = false ]; then
    echo "⚠ Azure Key Vault credentials look unset or placeholder."
    echo "  Preflight and deployment checks may fail until .env contains real values:"
    echo "  - AZURE_CLIENT_ID"
    echo "  - AZURE_TENANT_ID"
    echo "  - AZURE_CLIENT_SECRET"
fi

echo "📦 Starting Docker Compose Services..."
echo "   - PostgreSQL (port 5432)"
echo "   - Backend API (port 5000)"
echo "   - Frontend (port 3000)"
echo "   - Worker (port 5001, placeholder)"
echo ""

# Start services with default profile
docker-compose --profile dev up -d

echo ""
echo "⏳ Waiting for services to be ready..."
echo ""

# Wait for PostgreSQL
echo -n "   PostgreSQL: "
for i in {1..30}; do
    if docker-compose exec -T postgres pg_isready -U postgres > /dev/null 2>&1; then
        echo "✓ Ready"
        break
    fi
    if [ $i -eq 30 ]; then
        echo "✗ Timeout waiting for PostgreSQL"
        exit 1
    fi
    sleep 1
done

# Wait for Backend API
echo -n "   Backend API: "
for i in {1..60}; do
    if curl -s http://localhost:5000/health > /dev/null 2>&1; then
        echo "✓ Ready"
        break
    fi
    if [ $i -eq 60 ]; then
        echo "⚠ Backend may not be ready yet (still starting)"
        break
    fi
    sleep 1
done

# Wait for Frontend
echo -n "   Frontend: "
for i in {1..60}; do
    if curl -s http://localhost:3000 > /dev/null 2>&1; then
        echo "✓ Ready"
        break
    fi
    if [ $i -eq 60 ]; then
        echo "⚠ Frontend may not be ready yet (still starting)"
        break
    fi
    sleep 1
done

# Optional readiness check for Key Vault access when Azure credentials are configured
if [ "$HAS_REAL_AZURE_CREDS" = true ]; then
    echo -n "   Backend Readiness (/health/ready): "
    ready_ok=false
    for i in {1..30}; do
        ready_code=$(curl -s -o /tmp/azselfservice_ready.json -w "%{http_code}" http://localhost:5000/health/ready || true)
        if [ "$ready_code" = "200" ]; then
            echo "✓ Ready"
            ready_ok=true
            break
        fi
        sleep 1
    done

    if [ "$ready_ok" = false ]; then
        echo "⚠ Not ready"
        if [ -f /tmp/azselfservice_ready.json ]; then
            echo "   /health/ready response:"
            cat /tmp/azselfservice_ready.json
        fi
        echo "   Deployment preflight may fail until readiness is healthy."
    fi
fi

echo ""
echo "✅ Services Started Successfully!"
echo ""
echo "🌐 Access Points:"
echo "   Frontend:     http://localhost:3000"
echo "   Backend API:  http://localhost:5000"
echo "   API Docs:     http://localhost:5000/swagger"
echo "   Readiness:    http://localhost:5000/health/ready"
echo "   PostgreSQL:   localhost:5432"
echo ""
echo "🔐 Test Credentials:"
echo "   Username: admin"
echo "   Password: Test@1234"
echo ""
echo "📋 Useful Commands:"
echo "   View logs:           docker-compose logs -f backend"
echo "   Stop services:       ./scripts/dev-down.sh"
echo "   Database shell:      docker-compose exec postgres psql -U postgres -d azselfservice"
echo "   Backend shell:       docker-compose exec backend bash"
echo ""
echo "📖 Documentation:"
echo "   - API Reference: http://localhost:5000/swagger"
echo "   - Architecture: docs/architecture/solution-overview.md"
echo ""
