﻿namespace EcommerceApi.Models
{
    public partial class PurchaseDetail
    {
        public int PurchaseDetailId { get; set; }
        public int PurchaseId { get; set; }
        public int ProductId { get; set; }
        public decimal Amount { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal TotalPrice { get; set; }

        public Purchase Purchase { get; set; }
        public Product Product { get; set; }
    }
}
