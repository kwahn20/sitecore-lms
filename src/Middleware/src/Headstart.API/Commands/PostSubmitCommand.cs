using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Flurl.Http;
using Newtonsoft.Json;
using OrderCloud.SDK;
using Headstart.Common.Exceptions;
using Headstart.Models;
using Headstart.Common.Services;
using Headstart.Models.Headstart;
using ordercloud.integrations.library;
using Headstart.Models.Extended;
using Headstart.Common.Constants;
using Headstart.Common.Services.ShippingIntegration.Models;
using Headstart.Common;
using Headstart.API.Commands.Zoho;
using OrderCloud.Catalyst;

namespace Headstart.API.Commands
{
    public interface IPostSubmitCommand
    {
        Task<OrderSubmitResponse> HandleBuyerOrderSubmit(HSOrderWorksheet order);
        Task<OrderSubmitResponse> HandleZohoRetry(string orderID);
        Task<OrderSubmitResponse> HandleShippingValidate(string orderID, DecodedToken decodedToken);
    }

    public class PostSubmitCommand : IPostSubmitCommand
    {
        private readonly IOrderCloudClient _oc;
        private readonly IZohoCommand _zoho;
        private readonly ordercloud.integrations.library.ITaxCalculator _taxCalculator;
        private readonly ISendgridService _sendgridService;
        private readonly ILineItemCommand _lineItemCommand;
        private readonly AppSettings _settings;

        public PostSubmitCommand(
            ISendgridService sendgridService,
            ordercloud.integrations.library.ITaxCalculator taxCalculator,
            IOrderCloudClient oc,
            IZohoCommand zoho,
            ILineItemCommand lineItemCommand,
            AppSettings settings
        )
        {
            _oc = oc;
            _taxCalculator = taxCalculator;
            _zoho = zoho;
            _sendgridService = sendgridService;
            _lineItemCommand = lineItemCommand;
            _settings = settings;
        }

        public async Task<OrderSubmitResponse> HandleShippingValidate(string orderID, DecodedToken decodedToken)
        {
            var worksheet = await _oc.IntegrationEvents.GetWorksheetAsync<HSOrderWorksheet>(OrderDirection.Incoming, orderID);
            return await CreateOrderSubmitResponse(
                new List<ProcessResult>() { new ProcessResult()
                {
                    Type = ProcessType.Accounting,
                    Activity = new List<ProcessResultAction>() { await ProcessActivityCall(
                        ProcessType.Shipping,
                        "Validate Shipping",
                        ValidateShipping(worksheet)) }
                }},
                new List<HSOrder> { worksheet.Order });
        }

        public async Task<OrderSubmitResponse> HandleZohoRetry(string orderID)
        {
            var worksheet = await _oc.IntegrationEvents.GetWorksheetAsync<HSOrderWorksheet>(OrderDirection.Incoming, orderID);
            var supplierOrders = await Throttler.RunAsync(worksheet.LineItems.GroupBy(g => g.SupplierID).Select(s => s.Key), 100, 10, item => _oc.Orders.GetAsync<HSOrder>(OrderDirection.Outgoing,
                $"{worksheet.Order.ID}-{item}"));

            return await CreateOrderSubmitResponse(
                new List<ProcessResult>() { await this.PerformZohoTasks(worksheet, supplierOrders) }, 
                new List<HSOrder> { worksheet.Order });
        }

        private async Task<ProcessResult> PerformZohoTasks(HSOrderWorksheet worksheet, IList<HSOrder> supplierOrders)
        {
            var (salesAction, zohoSalesOrder) = await ProcessActivityCall(
                ProcessType.Accounting,
                "Create Zoho Sales Order",
                _zoho.CreateSalesOrder(worksheet));

            var (poAction, zohoPurchaseOrder) = await ProcessActivityCall(
                ProcessType.Accounting,
                "Create Zoho Purchase Order",
                _zoho.CreateOrUpdatePurchaseOrder(zohoSalesOrder, supplierOrders.ToList()));

            var (shippingAction, zohoShippingOrder) = await ProcessActivityCall(
                ProcessType.Accounting,
                "Create Zoho Shipping Purchase Order",
                _zoho.CreateShippingPurchaseOrder(zohoSalesOrder, worksheet));
            return new ProcessResult()
            {
                Type = ProcessType.Accounting,
                Activity = new List<ProcessResultAction>() {salesAction, poAction, shippingAction}
            };
        }

        public async Task<OrderSubmitResponse> HandleBuyerOrderSubmit(HSOrderWorksheet orderWorksheet)
        {
            var results = new List<ProcessResult>();

            //STEP 1
            var (supplierOrders, buyerOrder, activities) = await HandlingForwarding(orderWorksheet);
            results.Add(new ProcessResult()
            {
                Type = ProcessType.Forwarding,
                Activity = activities
            });
            //step 1 failed.we don't want to attempt the integrations. return error for further action

            if (activities.Any(a => !a.Success))
                return await CreateOrderSubmitResponse(results, new List<HSOrder> { orderWorksheet.Order });

            // STEP 2 (integrations)
            var integrations = await HandleIntegrations(orderWorksheet);
            results.AddRange(integrations);

            // STEP 3: return OrderSubmitResponse
            return await CreateOrderSubmitResponse(results, new List<HSOrder> { orderWorksheet.Order });
        }

        private async Task<List<ProcessResult>> HandleIntegrations(HSOrderWorksheet orderWorksheet)
        {
            
            var results = new List<ProcessResult>();

            //internal (sitecore.com/net) or external
            var externalBuyer = IsExternalBuyer(orderWorksheet.Order.FromUser.Email);

            // external notifications
            if (externalBuyer)
            {
                var isCCPayment = orderWorksheet?.Order?.xp?.StripePaymentId != null;
                var includesCert = ContainsCertProducts(orderWorksheet);


                if (isCCPayment && includesCert)
                {
                    
                    var notifications = await ProcessActivityCall(
                    ProcessType.Notification,
                    "Sending Order Submit Emails",
                    _sendgridService.SendCertOrderEmail(orderWorksheet));
                    results.Add(new ProcessResult()
                    {
                        Type = ProcessType.Notification,
                        Activity = new List<ProcessResultAction>() { notifications }
                    });

                } else if(!isCCPayment)
                {
                    //send template/data for all POs
                    var notifications = await ProcessActivityCall(
                    ProcessType.Notification,
                    "Sending Order Submit Emails",
                    _sendgridService.SendPurchaseOrderUpload(orderWorksheet, orderWorksheet.Order.xp?.POFileID));
                    results.Add(new ProcessResult()
                    {
                        Type = ProcessType.Notification,
                        Activity = new List<ProcessResultAction>() { notifications }
                    });
                }
                else return results;
            }

            //// STEP 2: Tax transaction
            var tax = await ProcessActivityCall(
                ProcessType.Tax,
                "Creating Tax Transaction",
                HandleTaxTransactionCreationAsync(orderWorksheet.Reserialize<OrderWorksheet>()));
            results.Add(new ProcessResult()
            {
                Type = ProcessType.Tax,
                Activity = new List<ProcessResultAction>() { tax }
            });

            // STEP 3: Validate shipping
            var shipping = await ProcessActivityCall(
                ProcessType.Shipping,
                "Validate Shipping",
                ValidateShipping(orderWorksheet));
            results.Add(new ProcessResult()
            {
                Type = ProcessType.Shipping,
                Activity = new List<ProcessResultAction>() { shipping }
            });

            return results;
        }
        
        private async Task<OrderSubmitResponse> CreateOrderSubmitResponse(List<ProcessResult> processResults, List<HSOrder> ordersRelatingToProcess)
        {
            try
            {
                if (processResults.All(i => i.Activity.All(a => a.Success)))
                {
                    await UpdateOrderNeedingAttention(ordersRelatingToProcess, false);
                    return new OrderSubmitResponse()
                    {
                        HttpStatusCode = 200,
                        xp = new OrderSubmitResponseXp()
                        {
                            ProcessResults = processResults
                        }
                    };
                }
                    
                await UpdateOrderNeedingAttention(ordersRelatingToProcess, true); 
                return new OrderSubmitResponse()
                {
                    HttpStatusCode = 500,
                    xp = new OrderSubmitResponseXp()
                    {
                        ProcessResults = processResults
                    }
                };
            }
            catch (OrderCloudException ex)
            {
                return new OrderSubmitResponse()
                {
                    HttpStatusCode = 500,
                    UnhandledErrorBody = JsonConvert.SerializeObject(ex.Errors)
                };
            }
        }
        
        private async Task UpdateOrderNeedingAttention(IList<HSOrder> orders, bool isError)
        {
            var partialOrder = new PartialOrder() { xp = new { NeedsAttention = isError } };

            var orderInfos = new List<Tuple<OrderDirection, string>> { };

            var buyerOrder = orders.First();
            var ocPaymentsList = (await _oc.Payments.ListAsync<HSPayment>(OrderDirection.Incoming, buyerOrder.ID, filters: "Type=CreditCard"));
            var ocPayments = ocPaymentsList.Items;
            var ocPayment = ocPayments.Any() ? ocPayments[0] : null;
            if (ocPayment != null)
            {
                partialOrder = new PartialOrder() { xp = new { NeedsAttention = isError, StripePaymentID = ocPayment.xp.stripePaymentID} };
            }
            orderInfos.Add(new Tuple<OrderDirection, string>(OrderDirection.Incoming, buyerOrder.ID));
            orders.RemoveAt(0);
            orderInfos.AddRange(orders.Select(o => new Tuple<OrderDirection, string>(OrderDirection.Outgoing, o.ID)));

            await Throttler.RunAsync(orderInfos, 100, 3, (orderInfo) => _oc.Orders.PatchAsync(orderInfo.Item1, orderInfo.Item2, partialOrder));

        }

        private static async Task<ProcessResultAction> ProcessActivityCall(ProcessType type, string description, Task func)
        {
            try
            {
                await func;
                return new ProcessResultAction() {
                        ProcessType = type,
                        Description = description,
                        Success = true
                };
            }
            catch (CatalystBaseException integrationEx)
            {
                return new ProcessResultAction()
                {
                    Description = description,
                    ProcessType = type,
                    Success = false,
                    Exception = new ProcessResultException(integrationEx)
                };
            }
            catch (FlurlHttpException flurlEx)
            {
                return new ProcessResultAction()
                {
                    Description = description,
                    ProcessType = type,
                    Success = false,
                    Exception = new ProcessResultException(flurlEx)
                };
            }
            catch (Exception ex)
            {
                return new ProcessResultAction() {
                    Description = description,
                    ProcessType = type,
                    Success = false,
                    Exception = new ProcessResultException(ex)
                };
            }
        }

        private static async Task<Tuple<ProcessResultAction, T>> ProcessActivityCall<T>(ProcessType type, string description, Task<T> func) where T : class, new()
        {
            // T must be a class and be newable so the error response can be handled.
            try
            {
                return new Tuple<ProcessResultAction, T>(
                    new ProcessResultAction()
                    {
                        ProcessType = type,
                        Description = description,
                        Success = true
                    },
                    await func
                );
            }
            catch (CatalystBaseException integrationEx)
            {
                return new Tuple<ProcessResultAction, T>(new ProcessResultAction()
                {
                    Description = description,
                    ProcessType = type,
                    Success = false,
                    Exception = new ProcessResultException(integrationEx)
                }, new T());
            }
            catch (FlurlHttpException flurlEx)
            {
                return new Tuple<ProcessResultAction, T>(new ProcessResultAction()
                {
                    Description = description,
                    ProcessType = type,
                    Success = false,
                    Exception = new ProcessResultException(flurlEx)
                }, new T());
            }
            catch (Exception ex)
            {
                return new Tuple<ProcessResultAction, T>(new ProcessResultAction()
                {
                    Description = description,
                    ProcessType = type,
                    Success = false,
                    Exception = new ProcessResultException(ex)
                }, new T());
            }
        }

        private async Task<Tuple<List<HSOrder>, HSOrderWorksheet, List<ProcessResultAction>>> HandlingForwarding(HSOrderWorksheet orderWorksheet)
        {
            var activities = new List<ProcessResultAction>();

            // forwarding
            var (forwardAction, forwardedOrders) = await ProcessActivityCall(
                ProcessType.Forwarding,
                "OrderCloud API Order.ForwardAsync",
                _oc.Orders.ForwardAsync(OrderDirection.Incoming, orderWorksheet.Order.ID)
            );
            activities.Add(forwardAction);

            var supplierOrders = forwardedOrders.OutgoingOrders.ToList();

            // creating relationship between the buyer order and the supplier order
            // no relationship exists currently in the platform
            var (updateAction, hsOrders) = await ProcessActivityCall(
                ProcessType.Forwarding, "Create Order Relationships And Transfer XP",
                CreateOrderRelationshipsAndTransferXP(orderWorksheet, supplierOrders));
            activities.Add(updateAction);

            // need to get fresh order worksheet because this process has changed things about the worksheet
            var (getAction, hsOrderWorksheet) = await ProcessActivityCall(
                ProcessType.Forwarding, 
                "Get Updated Order Worksheet",
                _oc.IntegrationEvents.GetWorksheetAsync<HSOrderWorksheet>(OrderDirection.Incoming, orderWorksheet.Order.ID));
            activities.Add(getAction);

            return await Task.FromResult(new Tuple<List<HSOrder>, HSOrderWorksheet, List<ProcessResultAction>>(hsOrders, hsOrderWorksheet, activities));
        }

        public  async Task<List<HSOrder>> CreateOrderRelationshipsAndTransferXP(HSOrderWorksheet buyerOrder, List<Order> supplierOrders)
        {
            var payment = (await _oc.Payments.ListAsync(OrderDirection.Incoming, buyerOrder.Order.ID))?.Items?.FirstOrDefault();
            var updatedSupplierOrders = new List<HSOrder>();
            var supplierIDs = new List<string>();
            var lineItems = await _oc.LineItems.ListAllAsync(OrderDirection.Incoming, buyerOrder.Order.ID);
            var shipFromAddressIDs = lineItems.DistinctBy(li => li.ShipFromAddressID).Select(li => li.ShipFromAddressID).ToList();
            var region = FindRegion(buyerOrder.Order.BillingAddress.Country);
            foreach (var supplierOrder in supplierOrders)
            {
                supplierIDs.Add(supplierOrder.ToCompanyID);
                var shipFromAddressIDsForSupplierOrder = shipFromAddressIDs.Where(addressID => addressID != null && addressID.Contains(supplierOrder.ToCompanyID)).ToList();
                var supplier = await _oc.Suppliers.GetAsync<HSSupplier>(supplierOrder.ToCompanyID);
                var suppliersShipEstimates = buyerOrder.ShipEstimateResponse?.ShipEstimates?.Where(se => se.xp.SupplierID == supplier.ID);
                var supplierOrderPatch = new PartialOrder() {
                    ID = $"{buyerOrder.Order.ID}-{supplierOrder.ToCompanyID}",
                    xp = new OrderXp() {
                        ShipFromAddressIDs = shipFromAddressIDsForSupplierOrder,
                        SupplierIDs = new List<string>() { supplier.ID },
                        StopShipSync = false,
                        OrderType = buyerOrder.Order.xp.OrderType,
                        QuoteOrderInfo = buyerOrder.Order.xp.QuoteOrderInfo,
                        Currency = supplier.xp.Currency,
                        ClaimStatus = ClaimStatus.NoClaim,
                        ShippingStatus = ShippingStatus.Processing,
                        SubmittedOrderStatus = SubmittedOrderStatus.Open,
                        SelectedShipMethodsSupplierView = suppliersShipEstimates != null ? MapSelectedShipMethod(suppliersShipEstimates) : null,
                        // ShippingAddress needed for Purchase Order Detail Report
                        ShippingAddress = new HSAddressBuyer()
                        {
                            ID = buyerOrder?.Order?.xp?.ShippingAddress?.ID,
                            CompanyName = buyerOrder?.Order?.xp?.ShippingAddress?.CompanyName,
                            FirstName = buyerOrder?.Order?.xp?.ShippingAddress?.FirstName,
                            LastName = buyerOrder?.Order?.xp?.ShippingAddress?.LastName,
                            Street1 = buyerOrder?.Order?.xp?.ShippingAddress?.Street1,
                            Street2 = buyerOrder?.Order?.xp?.ShippingAddress?.Street2,
                            City = buyerOrder?.Order?.xp?.ShippingAddress?.City,
                            State = buyerOrder?.Order?.xp?.ShippingAddress?.State,
                            Zip = buyerOrder?.Order?.xp?.ShippingAddress?.Zip,
                            Country = buyerOrder?.Order?.xp?.ShippingAddress?.Country,
                        }
            }
                };
                var updatedSupplierOrder = await _oc.Orders.PatchAsync<HSOrder>(OrderDirection.Outgoing, supplierOrder.ID, supplierOrderPatch);
                var supplierLineItems = lineItems.Where(li => li.SupplierID == supplier.ID).ToList();
                await SaveShipMethodByLineItem(supplierLineItems, supplierOrderPatch.xp.SelectedShipMethodsSupplierView, buyerOrder.Order.ID);
                await OverrideOutgoingLineQuoteUnitPrice(updatedSupplierOrder.ID, supplierLineItems);
                updatedSupplierOrders.Add(updatedSupplierOrder);
            }

            await _lineItemCommand.SetInitialSubmittedLineItemStatuses(buyerOrder.Order.ID);
            var sellerShipEstimates = buyerOrder.ShipEstimateResponse?.ShipEstimates?.Where(se => se.xp.SupplierID == null);

            //Patch Buyer Order after it has been submitted
            var buyerOrderPatch = new PartialOrder() {
                xp = new {
                    ShipFromAddressIDs = shipFromAddressIDs,
                    SupplierIDs = supplierIDs,
                    ClaimStatus = ClaimStatus.NoClaim,
                    ShippingStatus = ShippingStatus.Processing,
                    SubmittedOrderStatus = SubmittedOrderStatus.Open,
                    HasSellerProducts = buyerOrder.LineItems.Any(li => li.SupplierID == null),
                    PaymentMethod = payment.Type == PaymentType.CreditCard ? "Credit Card" : "Purchase Order",
                    OrderOnBehalfOf = buyerOrder.Order.xp.OrderOnBehalfOf ?? false,
                    Region = region,
                }
            };

            await _oc.Orders.PatchAsync(OrderDirection.Incoming, buyerOrder.Order.ID, buyerOrderPatch);
            return updatedSupplierOrders;
        }

        private List<ShipMethodSupplierView> MapSelectedShipMethod(IEnumerable<HSShipEstimate> shipEstimates)
		{
            var selectedShipMethods = shipEstimates.Select(shipEstimate =>
            {
                var selected = shipEstimate.ShipMethods.FirstOrDefault(sm => sm.ID == shipEstimate.SelectedShipMethodID);
                return new ShipMethodSupplierView()
                {
                    EstimatedTransitDays = selected.EstimatedTransitDays,
                    Name = selected.Name,
                    ShipFromAddressID = shipEstimate.xp.ShipFromAddressID
                };
            }).ToList();
            return selectedShipMethods;
        }

        private string FindRegion(string countryCode)
        {
            List<string> AMS = new List<string>(){"ag", "ai", "aw", "bb", "bm", "bo", "br", "bs", "bz",
                "ca", "cl", "co", "cr", "cu", "dm", "do", "ec", "gd", "gf",
                "gp", "gt", "gy", "hn", "ht", "jm", "kn", "ky", "lc", "mf",
                "mq", "ms", "mx", "ni", "pa", "pe", "pr", "py", "sv", "tc",
                "tt", "us", "uy", "vc", "ve", "vg", "vi"};
            
            List<string> APJ = new List<string>(){"as", "bn", "fj", "fm", "gu", "hm", "in", "io", "jp",
                "ki", "la", "mh", "mm", "mn", "mo", "mp", "my", "nc", "nf",
                "nr", "nu", "nz", "pg", "ph", "pk", "pn", "pw", "sb", "sg",
                "sh", "tk", "tl", "to", "tv", "tw", "um", "vn", "vu", "wf", "ws"};
            
            List<string> EMEA = new List<string>(){"ad", "ae", "af", "al", "am", "ao", "aq", "ar", "at",
                "ax", "az", "ba", "be", "bf", "bg", "bh", "bi", "bj", "bl",
                "bv", "bw", "by", "cd", "cf", "cg", "ch", "ci", "ck", "cv",
                "cx", "cy", "cz", "de", "dj", "dk", "dz", "eh", "er", "es",
                "et", "fi", "fk", "fo", "fr", "ga", "gb", "ge", "gg", "gh",
                "gi", "gl", "gm", "gn", "gp", "gq", "gr", "gs", "gw", "hm",
                "hr", "hu", "ie", "il", "im", "iq", "ir", "is", "it", "je",
                "jo", "ke", "kg", "km", "kw", "lb", "li", "lr", "ls", "lt",
                "lu", "lv", "ly", "ma", "mc", "md", "me", "mg", "mk", "ml",
                "mr", "mt", "mu", "mv", "mw", "na", "ne", "ng", "nl", "no",
                "np", "om", "pl", "pm", "ps", "pt", "qa", "re", "ro", "rs",
                "ru", "rw", "sa", "sc", "sd", "se", "sh", "si", "sk", "sl",
                "sm", "sn", "so", "ss", "st", "sx", "sy", "sz", "td", "tf",
                "tg", "tn", "tz", "ua", "ug", "va", "ye", "yt", "za", "zm", "zw"};

            if (AMS.Contains(countryCode.ToLower())) return "AMS";
            if (APJ.Contains(countryCode.ToLower())) return "APJ";
            if (EMEA.Contains(countryCode.ToLower())) return "EMEA";
            else { return "N/A"; }

        }

        private async Task HandleTaxTransactionCreationAsync(OrderWorksheet orderWorksheet)
        {
            var promotions = await _oc.Orders.ListAllPromotionsAsync(OrderDirection.All, orderWorksheet.Order.ID);

            var taxCalculation = await _taxCalculator.CommitTransactionAsync(orderWorksheet, promotions);
            await _oc.Orders.PatchAsync<HSOrder>(OrderDirection.Incoming, orderWorksheet.Order.ID, new PartialOrder()
            {
                TaxCost = taxCalculation.TotalTax,  // Set this again just to make sure we have the most up to date info
                xp = new { ExternalTaxTransactionID = taxCalculation.ExternalTransactionID }
            });
        }

        private static async Task ValidateShipping(HSOrderWorksheet orderWorksheet)
        {
            if(orderWorksheet.ShipEstimateResponse.HttpStatusCode != 200)
                throw new Exception(orderWorksheet.ShipEstimateResponse.UnhandledErrorBody);

            if(orderWorksheet.ShipEstimateResponse.ShipEstimates.Any(s => s.SelectedShipMethodID == ShippingConstants.NoRatesID))
                throw new Exception("No shipping rates could be determined - fallback shipping rate of $20 3-day was used");

            await Task.CompletedTask;
        }

        private async Task SaveShipMethodByLineItem(List<LineItem> lineItems, List<ShipMethodSupplierView> shipMethods, string buyerOrderID)
        {
            if (shipMethods != null)
            {
                foreach (LineItem lineItem in lineItems)
                {
                    string shipFromID = lineItem.ShipFromAddressID;
                    if (shipFromID != null)
                    {
                        ShipMethodSupplierView shipMethod = shipMethods.Find(shipMethod => shipMethod.ShipFromAddressID == shipFromID);
                        string readableShipMethod = shipMethod.Name.Replace("_", " ");
                        PartialLineItem lineItemToPatch = new PartialLineItem { xp = new { ShipMethod = readableShipMethod } };
                        LineItem patchedLineItem = await _oc.LineItems.PatchAsync(OrderDirection.Incoming, buyerOrderID, lineItem.ID, lineItemToPatch);
                    }
                }
            }
        }

        private async Task OverrideOutgoingLineQuoteUnitPrice(string supplierOrderID, List<LineItem> supplierLineItems)
        {
            foreach (LineItem lineItem in supplierLineItems)
            {
                if (lineItem?.Product?.xp?.ProductType == ProductType.Quote.ToString())
                {
                    var patch = new PartialLineItem { UnitPrice = lineItem.UnitPrice };
                    await _oc.LineItems.PatchAsync(OrderDirection.Outgoing, supplierOrderID, lineItem.ID, patch);
                }
            }
        }
        private static Boolean IsExternalBuyer(string email)
        {
            var domain = email.Split("@")[1];
            if (domain == "sitecore.com" || domain == "sitecore.net")
            {
                return false;
            }
            return true;
        }

        private static Boolean ContainsCertProducts(HSOrderWorksheet orderWorksheet)
        {
            return orderWorksheet.LineItems.Any(li => li.xp.IsCertification == true);
        }
    };
}