namespace AzSelfService.API.Contracts
{
    public class CreateStorageAccountContract
    {
        public string Name { get; set; }
        public string Region { get; set; }
        public string ResourceGroup { get; set; }
    }

    public class StorageAccountDetailsContract
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Region { get; set; }
        public string ResourceGroup { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}