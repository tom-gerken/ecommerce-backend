﻿using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;
using Dapper;
using EcommerceApi.ViewModel;
using Microsoft.Extensions.Configuration;

namespace EcommerceApi.Repositories
{
    public class OrderRepository : IOrderRepository
    {
        private readonly IConfiguration _config;

        public IDbConnection Connection
        {
            get
            {
                return new SqlConnection(_config.GetConnectionString("defaultConnection"));
            }
        }

        public OrderRepository(IConfiguration config)
        {
            _config = config;
        }

        public async Task<IEnumerable<OrderViewModel>> GetOrders(int? locationId)
        {
            using (IDbConnection conn = Connection)
            {
                string query = $@"
                                    SELECT [Order].[OrderId]
                                          ,[CustomerId]
                                          ,[Order].[LocationId]
                                          ,[OrderDate]
                                          ,[Total]
                                          ,[SubTotal]
                                          ,[TotalDiscount]
                                          ,[DiscountPercentage]
                                          ,[DiscountAmount]
                                          ,[DiscountId]
                                          ,[PstNumber]
                                          ,[Notes]
                                          ,[PoNumber]
                                          ,[Status]
                                          ,[CreatedByUserId]
	                                      ,ISNULL(OrderPayment.PaidAmount, 0) AS PaidAmount,
	                                      Location.LocationName
                                    FROM [Order]
                                    INNER JOIN Location
	                                    ON Location.LocationId = [Order].LocationId
                                    LEFT JOIN 
	                                    ( SELECT OrderId, SUM(PaymentAmount) AS PaidAmount 
	                                      FROM OrderPayment
	                                      GROUP BY OrderId) AS OrderPayment
	                                    ON OrderPayment.OrderId = [Order].OrderId
                                    WHERE [Order].LocationId = @LocationId OR @LocationId IS NULL
                                    ORDER BY [Order].[OrderId] DESC
                                 ";
                conn.Open();
                return await conn.QueryAsync<OrderViewModel>(query, new { LocationId = locationId });
            }
        }
    }
}