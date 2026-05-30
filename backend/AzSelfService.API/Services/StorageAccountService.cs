using AzSelfService.API.Contracts;
using System;
using System.Collections.Generic;

namespace AzSelfService.API.Services
{
    public interface IStorageAccountService
    {
        IEnumerable<StorageAccountDetailsContract> GetAllStorageAccounts();
        StorageAccountDetailsContract CreateStorageAccount(CreateStorageAccountContract contract);
    }

    public class StorageAccountService : IStorageAccountService
    {
        public IEnumerable<StorageAccountDetailsContract> GetAllStorageAccounts()
        {
            // Placeholder logic for fetching storage accounts
            return new List<StorageAccountDetailsContract>();
        }

        public StorageAccountDetailsContract CreateStorageAccount(CreateStorageAccountContract contract)
        {
            // Placeholder logic for creating a storage account
            return new StorageAccountDetailsContract
            {
                Id = Guid.NewGuid(),
                Name = contract.Name,
                Region = contract.Region,
                ResourceGroup = contract.ResourceGroup,
                CreatedAt = DateTime.UtcNow
            };
        }
    }
}