﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EcommerceApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using EcommerceApi.ViewModel;
using EcommerceApi.Repositories;
using DinkToPdf.Contracts;
using DinkToPdf;
using EcommerceApi.Untilities;
using System.IO;
using EcommerceApi.Services;

namespace EcommerceApi.Controllers
{
    [Authorize]
    [Produces("application/json")]
    [Route("api/Orders")]
    public class OrdersController : Controller
    {
        private readonly EcommerceContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IOrderRepository _orderRepository;
        private readonly IConverter _converter;
        private readonly IEmailSender _emailSender;

        public OrdersController(EcommerceContext context,
                                UserManager<ApplicationUser> userManager,
                                IOrderRepository orderRepository,
                                IConverter converter,
                                IEmailSender emailSender)
        {
            _context = context;
            _userManager = userManager;
            _orderRepository = orderRepository;
            _converter = converter;
            _emailSender = emailSender;
        }

        // GET: api/Orders
        [HttpGet]
        public async Task<IEnumerable<OrderViewModel>> GetOrder(bool showAllOrders)
        {
            return await _orderRepository.GetOrders(showAllOrders, null);
        }

        // GET: api/Orders/Location/{locationId}
        [HttpGet("location/{locationId}")]
        public async Task<IEnumerable<OrderViewModel>> GetOrderByLocation([FromRoute] int locationId, bool showAllOrders)
        {
            return await _orderRepository.GetOrders(showAllOrders, locationId);
        }

        // GET: api/Orders/Customer/{customerId}
        [HttpGet("customer/{customerId}")]
        public async Task<IEnumerable<OrderViewModel>> GetOrderByCustomer([FromRoute] int customerId)
        {
            return await _orderRepository.GetOrdersByCustomer(customerId);
        }

        // GET: api/Orders/5
        [HttpGet("{id}")]
        public async Task<IActionResult> GetOrder([FromRoute] int id)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var order = await _context.Order.AsNoTracking()
                .Include(o => o.OrderDetail)
                    .ThenInclude(o =>o.Product)
                .Include(t => t.OrderTax)
                    .ThenInclude(t => t.Tax)
                .Include(o => o.OrderPayment)
                .Include(o => o.Customer)
                .Include(l => l.Location)
                .SingleOrDefaultAsync(m => m.OrderId == id);

            if (order == null)
            {
                return NotFound();
            }

            return Ok(order);
        }

        // PUT: api/Orders/5/Status
        [HttpPut("{id}/Status")]
        public async Task<IActionResult> PutOrder([FromRoute] int id, [FromBody] UpdateOrderStatus updateOrderStatus)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            if (updateOrderStatus == null || string.IsNullOrEmpty(updateOrderStatus.OrderStatus))
            {
                return BadRequest();
            }
            var date = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Pacific Standard Time");
            var order = await _context.Order.SingleOrDefaultAsync(m => m.OrderId == id);
            if (updateOrderStatus.OrderStatus.Equals(OrderStatus.Paid.ToString(), StringComparison.InvariantCultureIgnoreCase))
            {
                System.Security.Claims.ClaimsPrincipal currentUser = this.User;
                var userId = _userManager.GetUserId(User);
                order.OrderPayment.Add(
                    new OrderPayment
                    {
                        CreatedByUserId = userId,
                        CreatedDate = date,
                        PaymentAmount = order.Total,
                        PaymentDate = date,
                        PaymentTypeId = updateOrderStatus.PaymentTypeId
                    }
                );
            }

            // When order is marked as Draft from OnHold we should add them to inventory
            if (updateOrderStatus.OrderStatus == OrderStatus.Draft.ToString() &&
               order.Status == OrderStatus.OnHold.ToString())
            {
              var done = await AddToInventory(order, updateOrderStatus);
            }
            else
            {
                var done = await UpdateInventory(order);
            }

            order.Status = updateOrderStatus.OrderStatus;

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!OrderExists(id))
                {
                    return NotFound();
                }
                else
                {
                    throw;
                }
            }

            return Ok(order);
        }

        [HttpPut("{id}/Info")]
        public async Task<IActionResult> PutOrderInfo([FromRoute] int id, [FromBody] UpdateOrderInfo updateOrderInfo)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var order = await _context.Order.SingleOrDefaultAsync(m => m.OrderId == id);
            order.Notes = updateOrderInfo.Notes;
            order.PoNumber = updateOrderInfo.PoNumber;
            await _context.SaveChangesAsync();
            return Ok(order);
        }

        // PUT: api/Orders/5
        [HttpPut("{id}")]
        public async Task<IActionResult> PutOrder([FromRoute] int id, [FromBody] Order order)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            if (id != order.OrderId)
            {
                return BadRequest();
            }

            _context.Entry(order).State = EntityState.Modified;

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!OrderExists(id))
                {
                    return NotFound();
                }
                else
                {
                    throw;
                }
            }

            return NoContent();
        }

        // POST: api/Orders
        [HttpPost]
        public async Task<IActionResult> PostOrder([FromBody] Order order)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var user = await _context.Users.FirstOrDefaultAsync(u => u.AuthCode.Equals(order.AuthCode, StringComparison.InvariantCultureIgnoreCase));
            order.CreatedByUserId = user.Id;
            var date = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Pacific Standard Time");
            order.CreatedDate = date;
            order.OrderDate = date;
            order.Customer = null;
            order.Location = null;
            if (order.Status.Equals(OrderStatus.Paid.ToString(), StringComparison.InvariantCultureIgnoreCase) ||
               (order.Status.Equals(OrderStatus.Return.ToString(), StringComparison.InvariantCultureIgnoreCase) && await OriginalOrderWasPaid(order.OriginalOrderId)))
            {
                order.OrderPayment.Add(
                    new OrderPayment
                    {
                        CreatedByUserId = user.Id,
                        CreatedDate = order.CreatedDate,
                        PaymentAmount = order.Total,
                        PaymentDate = order.CreatedDate,
                        PaymentTypeId = order.PaymentTypeId
                    }
                );
            }

            var done = await UpdateInventory(order);

            _context.Order.Add(order);
            await _context.SaveChangesAsync();

            return CreatedAtAction("GetOrder", new { id = order.OrderId }, order);
        }

        private async Task<bool> OriginalOrderWasPaid(int? originalOrderId)
        {
            if (!originalOrderId.HasValue)
            {
                return false;
            }

            var originalOrder = await _context.Order.SingleOrDefaultAsync(m => m.OrderId == originalOrderId.Value);
            if (originalOrder != null && originalOrder.Status.Equals(OrderStatus.Paid.ToString(), StringComparison.InvariantCultureIgnoreCase)) {
                return true;
            }
            return false;
        }

        private async Task<bool> UpdateInventory(Order order)
        {
            if (order.Status == OrderStatus.Draft.ToString())
            {
                return true;
            }
            var date = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Pacific Standard Time");
            // if order is refund we add to inventory
            var addOrUpdate = -1;
            if (order.Status == OrderStatus.Return.ToString())
            {
                addOrUpdate = 1;
            }

            foreach (var item in order.OrderDetail)
            {
                var productInventory = await _context.ProductInventory.FirstOrDefaultAsync(m =>
                    m.ProductId == item.ProductId &&
                    m.LocationId == order.LocationId);

                if (productInventory != null)
                {
                    productInventory.Balance = productInventory.Balance + (addOrUpdate * item.Amount);
                    productInventory.ModifiedDate = date;
                }
            }
            return true;
        }

        private async Task<bool> AddToInventory(Order order, UpdateOrderStatus updateOrderStatus)
        {
            if (updateOrderStatus.OrderStatus != OrderStatus.Draft.ToString())
            {
                return true;
            }
            var date = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Pacific Standard Time");
            foreach (var item in order.OrderDetail)
            {
                var productInventory = await _context.ProductInventory.FirstOrDefaultAsync(m =>
                    m.ProductId == item.ProductId &&
                    m.LocationId == order.LocationId);

                if (productInventory != null)
                {
                    productInventory.Balance = productInventory.Balance + item.Amount;
                    productInventory.ModifiedDate = date;
                }
            }
            return true;
        }

        // GET: api/Orders
        [HttpGet("{orderId}/email")]
        public async Task<IActionResult> EmailOrder([FromRoute] int orderId, [FromQuery] string email)
        {
            var order = await _context.Order.AsNoTracking()
                .Include(o => o.OrderDetail)
                    .ThenInclude(o => o.Product)
                .Include(t => t.OrderTax)
                    .ThenInclude(t => t.Tax)
                .Include(o => o.OrderPayment)
                .Include(o => o.Customer)
                .Include(l => l.Location)
                .SingleOrDefaultAsync(m => m.OrderId == orderId);

            var globalSettings = new GlobalSettings
            {
                ColorMode = ColorMode.Color,
                Orientation = Orientation.Portrait,
                PaperSize = PaperKind.A4,
                Margins = new MarginSettings { Top = 10 },
                DocumentTitle = $"Order {order.OrderId}",
                // Out = @"C:\PDFCreator\Employee_Report.pdf"
            };

            var objectSettings = new ObjectSettings
            {
                PagesCount = true,
                HtmlContent = OrderTemplateGenerator.GetHtmlString(order, false),
                WebSettings = { DefaultEncoding = "utf-8", UserStyleSheet = Path.Combine(Directory.GetCurrentDirectory(), "assets", "invoice.css") },
                // HeaderSettings = { FontName = "Arial", FontSize = 9, Right = "Page [page] of [toPage]", Line = true },
                // FooterSettings = { FontName = "Arial", FontSize = 9, Line = true, Center = "Report Footer" }
            };

            var pdf = new HtmlToPdfDocument()
            {
                GlobalSettings = globalSettings,
                Objects = { objectSettings }
            };

            var file = _converter.Convert(pdf);
            var message = @"
Dear Customer,


Attached is your current invoice from LED Lights and Parts (Pixel Print Ltd). 

If you have a credit account this invoice will be marked Awaiting payment. 

If you already paid this invoice will be marked PAID. and no further action is required. 

If you requested the quote, the invoice will be marked HOLD. 

If you returned or exchanged the invoice will be marked as Return/Exchange or Credit. 

Thank you for working with LED Lights and Parts! We\'re happy to work with you to solve any of your lighting challenges. 

Sincerely,

Shaney

3695 East 1st Ave Vancouver, BC V5M 1C2

Tel: (604) 559-5000

Cel: (778) 839-3352

Fax: (604) 559-5008

www.lightsandparts.com | essi@lightsandparts.com
            ";
            var attachment = new MemoryStream(file);
            var attachmentName = $"Invoice No {order.OrderId}.pdf";
            var subject = $"Pixel Print Ltd (LED Lights and Parts) Invoice No {order.OrderId}";

            if (string.IsNullOrEmpty(email))
            {
                email = order.Customer.Email;
            }

            var orderToUpdateEmail = _context.Order.FirstOrDefault(o => o.OrderId == orderId);
            orderToUpdateEmail.Email = email;
            await _context.SaveChangesAsync();

            await _emailSender.SendEmailAsync(email, subject, null, message, attachment, attachmentName, true);
            return Ok();
        }

        // GET: api/Orders
        [HttpGet("{orderId}/print")]
        [AllowAnonymous]
        public async Task<FileResult> PrintOrder([FromRoute] int orderId)
        {
            var order = await _context.Order.AsNoTracking()
                .Include(o => o.OrderDetail)
                    .ThenInclude(o => o.Product)
                .Include(t => t.OrderTax)
                    .ThenInclude(t => t.Tax)
                .Include(o => o.OrderPayment)
                .Include(o => o.Customer)
                .Include(l => l.Location)
                .SingleOrDefaultAsync(m => m.OrderId == orderId);

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == order.CreatedByUserId);
            order.CreatedByUserName = user.GivenName;

            var globalSettings = new GlobalSettings
            {
                ColorMode = ColorMode.Color,
                Orientation = Orientation.Portrait,
                PaperSize = PaperKind.A4,
                Margins = new MarginSettings { Top = 10 },
                DocumentTitle = $"Order {order.OrderId}",
                // Out = @"C:\PDFCreator\Employee_Report.pdf"
            };

            var objectSettings = new ObjectSettings
            {
                PagesCount = true,
                HtmlContent = OrderTemplateGenerator.GetHtmlString(order, true),
                WebSettings = { DefaultEncoding = "utf-8", UserStyleSheet = Path.Combine(Directory.GetCurrentDirectory(), "assets", "invoice.css") },
                // HeaderSettings = { FontName = "Arial", FontSize = 9, Right = "Page [page] of [toPage]", Line = true },
                // FooterSettings = { FontName = "Arial", FontSize = 9, Line = true, Center = "Page [page] of [toPage]" }
            };

            var pdf = new HtmlToPdfDocument()
            {
                GlobalSettings = globalSettings,
                Objects = { objectSettings }
            };

            // _converter.Convert(pdf);
            var file = _converter.Convert(pdf);
            FileContentResult result = new FileContentResult(file, "application/pdf")
            {
                FileDownloadName = $"Order-{order.OrderId}.pdf"
            };

            return result;
        }

        [HttpGet("customerinvoices")]
        [AllowAnonymous]
        public async Task<IActionResult> SendCustomerInvoices()
        {
            // get all customers that have pending orders in the previous month
            // find all orders (paid and unpaid) for these customers
            // send invoice emails to each customer with summary of paid/unpaid invoices
            // cc administrators
            // question: where is due date coming from?

//            var order = await _context.Order.AsNoTracking()
//                .Include(o => o.OrderDetail)
//                    .ThenInclude(o => o.Product)
//                .Include(t => t.OrderTax)
//                    .ThenInclude(t => t.Tax)
//                .Include(o => o.OrderPayment)
//                .Include(o => o.Customer)
//                .Include(l => l.Location)
//                .SingleOrDefaultAsync(m => m.OrderId == 1);

//            var globalSettings = new GlobalSettings
//            {
//                ColorMode = ColorMode.Color,
//                Orientation = Orientation.Portrait,
//                PaperSize = PaperKind.A4,
//                Margins = new MarginSettings { Top = 10 },
//                DocumentTitle = $"Order {order.OrderId}",
//            };

//            var objectSettings = new ObjectSettings
//            {
//                PagesCount = true,
//                HtmlContent = OrderTemplateGenerator.GetHtmlString(order, false),
//                WebSettings = { DefaultEncoding = "utf-8", UserStyleSheet = Path.Combine(Directory.GetCurrentDirectory(), "assets", "invoice.css") },
//            };

//            var pdf = new HtmlToPdfDocument()
//            {
//                GlobalSettings = globalSettings,
//                Objects = { objectSettings }
//            };

//            var file = _converter.Convert(pdf);
//            var message = @"
//Dear Customer,

//Thank you for choosing LED Lights and Parts. Your e-statement for the month, January-2019 is attached in the email. For any specific invoice information, get back to us to receive a copy. Please contact us at +1(604) 559-5000 for any other queries. 

//Sincerely,

//Shaney

//3695 East 1st Ave Vancouver, BC V5M 1C2

//Tel: (604) 559-5000

//Cel: (778) 838-8070

//Fax: (604) 559-5008

//www.lightsandparts.com | sina@lightsandparts.com
//            ";
//            var attachment = new MemoryStream(file);
//            var attachmentName = $"Monthly Report - {order.OrderId}.pdf";
//            var subject = $"Pixel Print Ltd (LED Lights and Parts) Invoice No {order.OrderId}";

//            if (string.IsNullOrEmpty(email))
//            {
//                email = order.Customer.Email;
//            }

//            var orderToUpdateEmail = _context.Order.FirstOrDefault(o => o.OrderId == orderId);
//            orderToUpdateEmail.Email = email;
//            await _context.SaveChangesAsync();

//            await _emailSender.SendEmailAsync(email, subject, null, message, attachment, attachmentName, true);

            return Ok();
        }

        private bool OrderExists(int id)
        {
            return _context.Order.Any(e => e.OrderId == id);
        }
    }
}