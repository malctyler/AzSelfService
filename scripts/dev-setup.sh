#!/bin/bash
set -e

echo "🔧 AzSelfService Development Environment Setup"
echo "=============================================="
echo ""

# Check if .env exists, otherwise create it from the template.
if [ ! -f .env ]; then
    if [ -f .env.docker ]; then
        echo "📝 Creating .env from existing .env.docker..."
        cp .env.docker .env
    else
        echo "📝 Creating .env from .env.example..."
        cp .env.example .env
    fi
    echo "   ✓ .env created"
    echo ""
    echo "⚠️  Important: Review .env and update with your local Azure values"
    echo "   (the file is ignored by git and stays machine-local)"
    echo ""
else
    echo "✓ .env already exists"
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
echo "   ✓ .env configuration"
echo "   ✓ logs directory"
echo "   ✓ terraform working directory"
echo ""

echo "✅ Setup Complete!"
echo ""
echo "🚀 Next Steps:"
echo "   1. Review .env for Azure settings"
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
