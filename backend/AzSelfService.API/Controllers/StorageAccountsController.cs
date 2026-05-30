using Microsoft.AspNetCore.Mvc;
using AzSelfService.API.Contracts;
using AzSelfService.API.Services;

namespace AzSelfService.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class StorageAccountsController : ControllerBase
    {
        private readonly IStorageAccountService _storageAccountService;

        public StorageAccountsController(IStorageAccountService storageAccountService)
        {
            _storageAccountService = storageAccountService;
        }

        [HttpGet]
        public IActionResult GetAllStorageAccounts()
        {
            var storageAccounts = _storageAccountService.GetAllStorageAccounts();
            return Ok(storageAccounts);
        }

        [HttpPost]
        public IActionResult CreateStorageAccount([FromBody] CreateStorageAccountContract contract)
        {
            var result = _storageAccountService.CreateStorageAccount(contract);
            return CreatedAtAction(nameof(GetAllStorageAccounts), new { id = result.Id }, result);
        }
    }
}