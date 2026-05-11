#!/bin/bash
set -e

echo "🔧 AzSelfService Dev Container Post-Create Setup"
echo "=================================================="
echo ""

# Install global Node packages
echo "📦 Installing Node global packages..."
npm install -g npm@latest

# Install global .NET tools
echo "📦 Installing .NET global tools..."
dotnet tool install -g dotnet-ef --version 9.0.0 2>/dev/null || dotnet tool update -g dotnet-ef --version 9.0.0

# Create logs directory
echo "📁 Creating logs directory..."
mkdir -p logs

# Display setup summary
echo ""
echo "✅ Dev Container Setup Complete!"
echo ""
echo "📋 Setup Summary:"
echo "   - Node.js version: $(node --version)"
echo "   - npm version: $(npm --version)"
echo "   - .NET version: $(dotnet --version)"
echo "   - Terraform version: $(terraform --version | head -n 1)"
echo "   - PostgreSQL client version: $(psql --version)"
echo ""
echo "🚀 Next Steps:"
echo "   1. Run: ./scripts/dev-setup.sh"
echo "   2. Run: ./scripts/dev-up.sh"
echo "   3. Frontend: http://localhost:3000"
echo "   4. Backend API: http://localhost:5000/swagger"
echo "   5. Login: admin / Test@1234"
echo ""
echo "📖 Documentation:"
echo "   - Architecture: docs/architecture/solution-overview.md"
echo "   - Auth Model: docs/architecture/auth-model.md"
echo "   - Database: docs/architecture/database-design.md"
echo ""
