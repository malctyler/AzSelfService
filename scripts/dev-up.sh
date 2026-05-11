#!/bin/bash
set -e

echo "🚀 Starting AzSelfService Development Environment"
echo "=================================================="
echo ""

# Check if .env.docker exists
if [ ! -f .env.docker ]; then
    echo "❌ Error: .env.docker not found"
    echo "   Run: ./scripts/dev-setup.sh"
    exit 1
fi

# Load environment variables
export $(cat .env.docker | grep -v '^#' | xargs)

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

echo ""
echo "✅ Services Started Successfully!"
echo ""
echo "🌐 Access Points:"
echo "   Frontend:     http://localhost:3000"
echo "   Backend API:  http://localhost:5000"
echo "   API Docs:     http://localhost:5000/swagger"
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
