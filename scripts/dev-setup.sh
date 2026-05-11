#!/bin/bash
set -e

echo "🔧 AzSelfService Development Environment Setup"
echo "=============================================="
echo ""

# Check if .env.docker exists, if not create it
if [ ! -f .env.docker ]; then
    echo "📝 Creating .env.docker from .env.example..."
    cp .env.example .env.docker
    echo "   ✓ .env.docker created"
    echo ""
    echo "⚠️  Important: Review .env.docker and update with your values"
    echo "   (especially Azure credentials for production)"
    echo ""
else
    echo "✓ .env.docker already exists"
fi

# Create logs directory
if [ ! -d logs ]; then
    echo "📁 Creating logs directory..."
    mkdir -p logs
    echo "   ✓ logs directory created"
fi

# Create terraform working directory
if [ ! -d /tmp/terraform ]; then
    echo "📁 Creating terraform working directory..."
    mkdir -p /tmp/terraform
    echo "   ✓ /tmp/terraform directory created"
fi

echo ""
echo "📋 Setup Checklist:"
echo "   ✓ .env.docker configuration"
echo "   ✓ logs directory"
echo "   ✓ terraform working directory"
echo ""

echo "✅ Setup Complete!"
echo ""
echo "🚀 Next Steps:"
echo "   1. Review .env.docker for Azure settings"
echo "   2. Run: ./scripts/dev-up.sh"
echo "   3. Wait for all services to start (PostgreSQL, Backend, Frontend)"
echo "   4. Access frontend at: http://localhost:3000"
echo "   5. Access backend API at: http://localhost:5000/swagger"
echo "   6. Login with: admin / Test@1234"
echo ""
echo "📖 Documentation:"
echo "   - Getting Started: README.md"
echo "   - Architecture: docs/architecture/solution-overview.md"
echo "   - Stop containers: ./scripts/dev-down.sh"
echo ""
