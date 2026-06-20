#!/bin/bash
set -e

echo "📊 Initializing AzSelfService Database"
echo "====================================="
echo ""

# Create application user (if not postgres)
if [ "$POSTGRES_USER" != "postgres" ]; then
    psql -v ON_ERROR_STOP=1 --username "postgres" --dbname "postgres" <<-EOSQL
        CREATE ROLE $POSTGRES_USER WITH CREATEDB LOGIN ENCRYPTED PASSWORD '$POSTGRES_PASSWORD';
        ALTER ROLE $POSTGRES_USER CREATEDB;
        ALTER ROLE $POSTGRES_USER WITH CREATEROLE;
EOSQL
    echo "✓ Application user created"
else
    echo "✓ Using postgres superuser"
fi

# Connect to the application database and create schema
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL

    -- Enable extensions
    CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
    CREATE EXTENSION IF NOT EXISTS "pg_trgm";
    
    -- Customers table (multi-tenancy root)
    CREATE TABLE IF NOT EXISTS customers (
        id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
        name VARCHAR(255) NOT NULL,
        subscription_id VARCHAR(255) NOT NULL UNIQUE,
        tenant_id VARCHAR(255) NOT NULL,
        sp_client_id_secret_ref VARCHAR(1024),
        sp_client_secret_secret_ref VARCHAR(1024),
        sp_tenant_id_secret_ref VARCHAR(1024),
        sp_subscription_id_secret_ref VARCHAR(1024),
        created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
        updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
        is_active BOOLEAN DEFAULT TRUE
    );
    CREATE INDEX IF NOT EXISTS idx_customers_subscription_id ON customers(subscription_id);
    CREATE INDEX IF NOT EXISTS idx_customers_tenant_id ON customers(tenant_id);
    COMMENT ON TABLE customers IS 'Customer organizations - root of multi-tenant hierarchy';

    -- Backward-compatible upgrades for existing dev databases
    ALTER TABLE customers ADD COLUMN IF NOT EXISTS sp_client_id_secret_ref VARCHAR(1024);
    ALTER TABLE customers ADD COLUMN IF NOT EXISTS sp_client_secret_secret_ref VARCHAR(1024);
    ALTER TABLE customers ADD COLUMN IF NOT EXISTS sp_tenant_id_secret_ref VARCHAR(1024);
    ALTER TABLE customers ADD COLUMN IF NOT EXISTS sp_subscription_id_secret_ref VARCHAR(1024);

    -- Users table
    CREATE TABLE IF NOT EXISTS users (
        id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
        customer_id UUID NOT NULL REFERENCES customers(id) ON DELETE CASCADE,
        username VARCHAR(255) NOT NULL,
        password_hash VARCHAR(255) NOT NULL,
        email VARCHAR(255),
        created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
        updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
        is_active BOOLEAN DEFAULT TRUE,
        UNIQUE(customer_id, username)
    );
    CREATE INDEX IF NOT EXISTS idx_users_customer_id ON users(customer_id);
    CREATE INDEX IF NOT EXISTS idx_users_username ON users(username);
    COMMENT ON TABLE users IS 'Users scoped to customers - authentication principals';

    -- Modules table (self-describing Terraform modules)
    CREATE TABLE IF NOT EXISTS modules (
        id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
        name VARCHAR(255) NOT NULL,
        version VARCHAR(50) NOT NULL,
        terraform_path VARCHAR(512) NOT NULL,
        schema JSONB NOT NULL,
        ui_schema JSONB,
        description TEXT,
        created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
        updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
        is_published BOOLEAN DEFAULT FALSE,
        is_deprecated BOOLEAN DEFAULT FALSE,
        UNIQUE(name, version)
    );
    CREATE INDEX IF NOT EXISTS idx_modules_name ON modules(name);
    CREATE INDEX IF NOT EXISTS idx_modules_is_published ON modules(is_published);
    COMMENT ON TABLE modules IS 'Terraform modules as self-describing products';

    -- Allowed regions table (shared location dropdown configuration)
    CREATE TABLE IF NOT EXISTS allowed_regions (
        code VARCHAR(64) PRIMARY KEY,
        sort_order INTEGER NOT NULL DEFAULT 0,
        created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
        updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
    );
    CREATE INDEX IF NOT EXISTS idx_allowed_regions_sort_order ON allowed_regions(sort_order);
    COMMENT ON TABLE allowed_regions IS 'Shared Azure region catalog used across module location dropdowns';

    -- Deployments table (audit trail)
    CREATE TABLE IF NOT EXISTS deployments (
        id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
        customer_id UUID NOT NULL REFERENCES customers(id) ON DELETE CASCADE,
        module_id UUID NOT NULL REFERENCES modules(id) ON DELETE RESTRICT,
        requested_by UUID NOT NULL REFERENCES users(id) ON DELETE SET NULL,
        status VARCHAR(50) NOT NULL DEFAULT 'QUEUED',
        error_message TEXT,
        retry_count INTEGER DEFAULT 0,
        terraform_state_path VARCHAR(512),
        created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
        updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
        completed_at TIMESTAMP,
        CHECK (status IN ('QUEUED', 'RUNNING', 'SUCCEEDED', 'FAILED', 'ROLLED_BACK'))
    );
    CREATE INDEX IF NOT EXISTS idx_deployments_customer_id ON deployments(customer_id);
    CREATE INDEX IF NOT EXISTS idx_deployments_module_id ON deployments(module_id);
    CREATE INDEX IF NOT EXISTS idx_deployments_status ON deployments(status);
    CREATE INDEX IF NOT EXISTS idx_deployments_created_at ON deployments(created_at);
    COMMENT ON TABLE deployments IS 'Deployment jobs - audit trail of all provisioning';

    -- Deployment inputs (flexible schema)
    CREATE TABLE IF NOT EXISTS deployment_inputs (
        id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
        deployment_id UUID NOT NULL UNIQUE REFERENCES deployments(id) ON DELETE CASCADE,
        inputs JSONB NOT NULL,
        created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
    );
    COMMENT ON TABLE deployment_inputs IS 'User-provided inputs for each deployment - flexible schema via JSONB';

    -- Deployment outputs (Terraform outputs)
    CREATE TABLE IF NOT EXISTS deployment_outputs (
        id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
        deployment_id UUID NOT NULL UNIQUE REFERENCES deployments(id) ON DELETE CASCADE,
        outputs JSONB NOT NULL,
        created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
    );
    COMMENT ON TABLE deployment_outputs IS 'Terraform outputs persisted after successful deployment';

    -- Deployment logs (real-time streaming)
    CREATE TABLE IF NOT EXISTS deployment_logs (
        id BIGSERIAL PRIMARY KEY,
        deployment_id UUID NOT NULL REFERENCES deployments(id) ON DELETE CASCADE,
        timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
        level VARCHAR(20) NOT NULL DEFAULT 'INFO',
        message TEXT NOT NULL,
        context JSONB
    );
    CREATE INDEX IF NOT EXISTS idx_deployment_logs_deployment_id_timestamp 
        ON deployment_logs(deployment_id, timestamp);
    COMMENT ON TABLE deployment_logs IS 'Terraform execution logs - real-time streaming to frontend';

    -- Audit logs (compliance & security)
    CREATE TABLE IF NOT EXISTS audit_logs (
        id BIGSERIAL PRIMARY KEY,
        timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
        customer_id UUID NOT NULL REFERENCES customers(id) ON DELETE CASCADE,
        actor_id UUID REFERENCES users(id) ON DELETE SET NULL,
        action VARCHAR(50) NOT NULL,
        resource_type VARCHAR(100),
        resource_id VARCHAR(255),
        before_state JSONB,
        after_state JSONB,
        ip_address VARCHAR(45),
        user_agent TEXT
    );
    CREATE INDEX IF NOT EXISTS idx_audit_logs_customer_id ON audit_logs(customer_id);
    CREATE INDEX IF NOT EXISTS idx_audit_logs_timestamp ON audit_logs(timestamp);
    CREATE INDEX IF NOT EXISTS idx_audit_logs_action ON audit_logs(action);
    COMMENT ON TABLE audit_logs IS 'Immutable audit trail for compliance - all state changes logged';

    -- Seed data: Default customer and admin user
    INSERT INTO customers (name, subscription_id, tenant_id, is_active) 
    VALUES ('Development Tenant', 'dev-subscription-123', 'dev-tenant-id', TRUE)
    ON CONFLICT (subscription_id) DO NOTHING;

    -- Store only Key Vault secret references (never secret values) for default tenant
    UPDATE customers
    SET
        sp_client_id_secret_ref = COALESCE(sp_client_id_secret_ref, 'customers/' || id || '/sp-client-id'),
        sp_client_secret_secret_ref = COALESCE(sp_client_secret_secret_ref, 'customers/' || id || '/sp-client-secret'),
        sp_tenant_id_secret_ref = COALESCE(sp_tenant_id_secret_ref, 'customers/' || id || '/sp-tenant-id'),
        sp_subscription_id_secret_ref = COALESCE(sp_subscription_id_secret_ref, 'customers/' || id || '/sp-subscription-id')
    WHERE subscription_id = 'dev-subscription-123';

    -- Seed admin user (password: Test@1234, bcrypt hash with cost 10)
    INSERT INTO users (customer_id, username, password_hash, email, is_active)
    SELECT id, 'admin', '\$2a\$10\$3VSbv.zs5C1v0jscI8HdVOVL8RrCqI3XKkHcVt3CHpZqXOcbKAy8e', 'admin@example.com', TRUE
    FROM customers 
    WHERE subscription_id = 'dev-subscription-123'
    ON CONFLICT (customer_id, username) DO NOTHING;

    INSERT INTO allowed_regions (code, sort_order)
    VALUES
        ('eastus', 0),
        ('westus', 1),
        ('eastus2', 2),
        ('westeurope', 3),
        ('southeastasia', 4),
        ('northeurope', 5)
    ON CONFLICT (code) DO NOTHING;

    -- Seed Resource Group module
    INSERT INTO modules (name, version, terraform_path, schema, ui_schema, description, is_published, is_deprecated)
    VALUES (
        'resource-group',
        '1.0.0',
        'terraform-modules/resource-group',
        '{"type": "object", "properties": {"name": {"type": "string", "minLength": 1, "pattern": "^[a-zA-Z0-9-_]*$"}, "location": {"type": "string", "enum": ["eastus", "westus", "eastus2", "westeurope", "southeastasia"]}}, "required": ["name", "location"]}',
        '{"fields": [{"name": "name", "label": "Resource Group Name", "placeholder": "my-resource-group", "help": "Alphanumeric, hyphens, underscores only"}, {"name": "location", "label": "Azure Region", "type": "select"}]}',
        'Azure Resource Group - foundation for all resources',
        TRUE,
        FALSE
    )
    ON CONFLICT (name, version) DO NOTHING;

    -- Seed Storage Account module
    INSERT INTO modules (name, version, terraform_path, schema, ui_schema, description, is_published, is_deprecated)
    VALUES (
        'storage-account',
        '1.0.0',
        'terraform-modules/storage-account',
        '{"type": "object", "properties": {"name": {"type": "string", "minLength": 3, "maxLength": 24, "pattern": "^[a-z0-9]{3,24}$"}, "resource_group_name": {"type": "string", "minLength": 1}, "location": {"type": "string", "enum": ["eastus", "westus", "eastus2", "westeurope", "southeastasia", "northeurope"]}, "account_tier": {"type": "string", "enum": ["Standard", "Premium"]}, "account_replication_type": {"type": "string", "enum": ["LRS", "GRS", "RAGRS", "ZRS", "GZRS", "RAGZRS"]}}, "required": ["name", "resource_group_name", "location"]}',
        '{"fields": [{"name": "name", "label": "Storage Account Name", "placeholder": "stazselfservicedev01", "help": "3-24 lowercase letters and numbers only"}, {"name": "resource_group_name", "label": "Resource Group Name", "placeholder": "rg-azselfservice-dev"}, {"name": "location", "label": "Azure Region", "type": "select"}, {"name": "account_tier", "label": "Performance Tier", "type": "select"}, {"name": "account_replication_type", "label": "Replication", "type": "select"}]}',
        'Azure Storage Account - general purpose v2 storage',
        TRUE,
        FALSE
    )
    ON CONFLICT (name, version) DO NOTHING;

EOSQL

echo "✓ Database schema created"
echo "✓ Default customer and admin user seeded"
echo "✓ Resource Group module registered"
echo "✓ Storage Account module registered"
echo ""
echo "🔐 Test Login:"
echo "   Username: admin"
echo "   Password: Test@1234"
echo ""
