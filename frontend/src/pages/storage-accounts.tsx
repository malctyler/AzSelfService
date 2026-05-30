import React, { useEffect, useState } from 'react';
import { getStorageAccounts, createStorageAccount, StorageAccount } from '../lib/api';

const StorageAccountsPage = () => {
    const [storageAccounts, setStorageAccounts] = useState<StorageAccount[]>([]);
    const [newStorageAccount, setNewStorageAccount] = useState<Omit<StorageAccount, 'id' | 'createdAt'>>({ name: '', region: '', resourceGroup: '' });

    useEffect(() => {
        const fetchStorageAccounts = async () => {
            const data = await getStorageAccounts();
            setStorageAccounts(data);
        };
        fetchStorageAccounts();
    }, []);

    const handleCreate = async () => {
        const createdAccount = await createStorageAccount(newStorageAccount);
        setStorageAccounts([...storageAccounts, createdAccount]);
        setNewStorageAccount({ name: '', region: '', resourceGroup: '' });
    };

    return (
        <div>
            <h1>Storage Accounts</h1>
            <ul>
                {storageAccounts.map(account => (
                    <li key={account.id}>{account.name} ({account.region})</li>
                ))}
            </ul>

            <h2>Create New Storage Account</h2>
            <input
                type="text"
                placeholder="Name"
                value={newStorageAccount.name}
                onChange={(e) => setNewStorageAccount({ ...newStorageAccount, name: e.target.value })}
            />
            <input
                type="text"
                placeholder="Region"
                value={newStorageAccount.region}
                onChange={(e) => setNewStorageAccount({ ...newStorageAccount, region: e.target.value })}
            />
            <input
                type="text"
                placeholder="Resource Group"
                value={newStorageAccount.resourceGroup}
                onChange={(e) => setNewStorageAccount({ ...newStorageAccount, resourceGroup: e.target.value })}
            />
            <button onClick={handleCreate}>Create</button>
        </div>
    );
};

export default StorageAccountsPage;