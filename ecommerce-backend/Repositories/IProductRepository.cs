﻿using EcommerceApi.ViewModel;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace EcommerceApi.Repositories
{
    public interface IProductRepository
    {
        Task<IEnumerable<ProductViewModel>> GetProducts();
        Task<IEnumerable<ProductViewModel>> GetAvailableProducts();
        Task<ProductViewModel> GetProduct(int productId);
        Task<IEnumerable<ProductTransactionViewModel>> GetProductTransactions(int productId);
    }
}
