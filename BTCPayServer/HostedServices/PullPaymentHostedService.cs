using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Services;
using BTCPayServer.Services.Notifications;
using BTCPayServer.Services.Notifications.Blobs;
using BTCPayServer.Services.Rates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBXplorer;
using PayoutData = BTCPayServer.Data.PayoutData;


namespace BTCPayServer.HostedServices
{
    public class CreatePullPayment
    {
        public DateTimeOffset? ExpiresAt { get; set; }
        public DateTimeOffset? StartsAt { get; set; }
        public string StoreId { get; set; }
        public string Name { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; }
        public string CustomCSSLink { get; set; }
        public string EmbeddedCSS { get; set; }
        public PaymentMethodId[] PaymentMethodIds { get; set; }
        public TimeSpan? Period { get; set; }
    }
    public class PullPaymentHostedService : BaseAsyncService
    {
        public class CancelRequest
        {
            public CancelRequest(string pullPaymentId)
            {
                ArgumentNullException.ThrowIfNull(pullPaymentId);
                PullPaymentId = pullPaymentId;
            }
            public CancelRequest(string[] payoutIds)
            {
                ArgumentNullException.ThrowIfNull(payoutIds);
                PayoutIds = payoutIds;
            }
            public string PullPaymentId { get; set; }
            public string[] PayoutIds { get; set; }
            internal TaskCompletionSource<bool> Completion { get; set; }
        }
        public class PayoutApproval
        {
            public enum Result
            {
                Ok,
                NotFound,
                InvalidState,
                TooLowAmount,
                OldRevision
            }
            public string PayoutId { get; set; }
            public int Revision { get; set; }
            public decimal Rate { get; set; }
            internal TaskCompletionSource<Result> Completion { get; set; }

            public static string GetErrorMessage(Result result)
            {
                switch (result)
                {
                    case PullPaymentHostedService.PayoutApproval.Result.Ok:
                        return "Ok";
                    case PullPaymentHostedService.PayoutApproval.Result.InvalidState:
                        return "The payout is not in a state that can be approved";
                    case PullPaymentHostedService.PayoutApproval.Result.TooLowAmount:
                        return "The crypto amount is too small.";
                    case PullPaymentHostedService.PayoutApproval.Result.OldRevision:
                        return "The crypto amount is too small.";
                    case PullPaymentHostedService.PayoutApproval.Result.NotFound:
                        return "The payout is not found";
                    default:
                        throw new NotSupportedException();
                }
            }
        }
        public async Task<string> CreatePullPayment(CreatePullPayment create)
        {
            ArgumentNullException.ThrowIfNull(create);
            if (create.Amount <= 0.0m)
                throw new ArgumentException("Amount out of bound", nameof(create));
            using var ctx = this._dbContextFactory.CreateContext();
            var o = new Data.PullPaymentData();
            o.StartDate = create.StartsAt is DateTimeOffset date ? date : DateTimeOffset.UtcNow;
            o.EndDate = create.ExpiresAt is DateTimeOffset date2 ? new DateTimeOffset?(date2) : null;
            o.Period = create.Period is TimeSpan period ? (long?)period.TotalSeconds : null;
            o.Id = Encoders.Base58.EncodeData(RandomUtils.GetBytes(20));
            o.StoreId = create.StoreId;
            o.SetBlob(new PullPaymentBlob()
            {
                Name = create.Name ?? string.Empty,
                Currency = create.Currency,
                Limit = create.Amount,
                Period = o.Period is long periodSeconds ? (TimeSpan?)TimeSpan.FromSeconds(periodSeconds) : null,
                SupportedPaymentMethods = create.PaymentMethodIds,
                View = new PullPaymentBlob.PullPaymentView()
                {
                    Title = create.Name ?? string.Empty,
                    Description = string.Empty,
                    CustomCSSLink = create.CustomCSSLink,
                    Email = null,
                    EmbeddedCSS = create.EmbeddedCSS,
                }
            });
            ctx.PullPayments.Add(o);
            await ctx.SaveChangesAsync();
            return o.Id;
        }

        public async Task<Data.PullPaymentData> GetPullPayment(string pullPaymentId, bool includePayouts)
        {
            await using var ctx = _dbContextFactory.CreateContext();
            IQueryable<Data.PullPaymentData> query = ctx.PullPayments;
            if (includePayouts)
                query = query.Include(data => data.Payouts);

            return await query.FirstOrDefaultAsync(data => data.Id == pullPaymentId);
        }

        class PayoutRequest
        {
            public PayoutRequest(TaskCompletionSource<ClaimRequest.ClaimResponse> completionSource, ClaimRequest request)
            {
                ArgumentNullException.ThrowIfNull(request);
                ArgumentNullException.ThrowIfNull(completionSource);
                Completion = completionSource;
                ClaimRequest = request;
            }
            public TaskCompletionSource<ClaimRequest.ClaimResponse> Completion { get; set; }
            public ClaimRequest ClaimRequest { get; }
        }
        public PullPaymentHostedService(ApplicationDbContextFactory dbContextFactory,
            BTCPayNetworkJsonSerializerSettings jsonSerializerSettings,
            CurrencyNameTable currencyNameTable,
            EventAggregator eventAggregator,
            BTCPayNetworkProvider networkProvider,
            NotificationSender notificationSender,
            RateFetcher rateFetcher,
            IEnumerable<IPayoutHandler> payoutHandlers,
            ILogger<PullPaymentHostedService> logger,
            Logs logs) : base(logs)
        {
            _dbContextFactory = dbContextFactory;
            _jsonSerializerSettings = jsonSerializerSettings;
            _currencyNameTable = currencyNameTable;
            _eventAggregator = eventAggregator;
            _networkProvider = networkProvider;
            _notificationSender = notificationSender;
            _rateFetcher = rateFetcher;
            _payoutHandlers = payoutHandlers;
            _logger = logger;
        }

        Channel<object> _Channel;
        private readonly ApplicationDbContextFactory _dbContextFactory;
        private readonly BTCPayNetworkJsonSerializerSettings _jsonSerializerSettings;
        private readonly CurrencyNameTable _currencyNameTable;
        private readonly EventAggregator _eventAggregator;
        private readonly BTCPayNetworkProvider _networkProvider;
        private readonly NotificationSender _notificationSender;
        private readonly RateFetcher _rateFetcher;
        private readonly IEnumerable<IPayoutHandler> _payoutHandlers;
        private readonly ILogger<PullPaymentHostedService> _logger;
        private readonly CompositeDisposable _subscriptions = new CompositeDisposable();

        internal override Task[] InitializeTasks()
        {
            _Channel = Channel.CreateUnbounded<object>();
            foreach (IPayoutHandler payoutHandler in _payoutHandlers)
            {
                payoutHandler.StartBackgroundCheck(Subscribe);
            }
            return new[] { Loop() };
        }

        private void Subscribe(params Type[] events)
        {
            foreach (Type @event in events)
            {
                _eventAggregator.Subscribe(@event, (subscription, o) => _Channel.Writer.TryWrite(o));
            }
        }

        private async Task Loop()
        {
            await foreach (var o in _Channel.Reader.ReadAllAsync())
            {
                if (o is PayoutRequest req)
                {
                    await HandleCreatePayout(req);
                }

                if (o is PayoutApproval approv)
                {
                    await HandleApproval(approv);
                }
                if (o is CancelRequest cancel)
                {
                    await HandleCancel(cancel);
                }
                if (o is InternalPayoutPaidRequest paid)
                {
                    await HandleMarkPaid(paid);
                }
                foreach (IPayoutHandler payoutHandler in _payoutHandlers)
                {
                    try
                    {
                        await payoutHandler.BackgroundCheck(o);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "PayoutHandler failed during BackgroundCheck");
                    }
                }
            }
        }

        public Task<RateResult> GetRate(PayoutData payout, string explicitRateRule, CancellationToken cancellationToken)
        {
            var ppBlob = payout.PullPaymentData.GetBlob();
            var currencyPair = new Rating.CurrencyPair(payout.GetPaymentMethodId().CryptoCode, ppBlob.Currency);
            Rating.RateRule rule = null;
            try
            {
                if (explicitRateRule is null)
                {
                    var storeBlob = payout.PullPaymentData.StoreData.GetStoreBlob();
                    var rules = storeBlob.GetRateRules(_networkProvider);
                    rules.Spread = 0.0m;
                    rule = rules.GetRuleFor(currencyPair);
                }
                else
                {
                    rule = Rating.RateRule.CreateFromExpression(explicitRateRule, currencyPair);
                }
            }
            catch (Exception)
            {
                throw new FormatException("Invalid RateRule");
            }
            return _rateFetcher.FetchRate(rule, cancellationToken);
        }
        public Task<PayoutApproval.Result> Approve(PayoutApproval approval)
        {
            approval.Completion = new TaskCompletionSource<PayoutApproval.Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_Channel.Writer.TryWrite(approval))
                throw new ObjectDisposedException(nameof(PullPaymentHostedService));
            return approval.Completion.Task;
        }
        private async Task HandleApproval(PayoutApproval req)
        {
            try
            {
                using var ctx = _dbContextFactory.CreateContext();
                var payout = await ctx.Payouts.Include(p => p.PullPaymentData).Where(p => p.Id == req.PayoutId).FirstOrDefaultAsync();
                if (payout is null)
                {
                    req.Completion.SetResult(PayoutApproval.Result.NotFound);
                    return;
                }
                if (payout.State != PayoutState.AwaitingApproval)
                {
                    req.Completion.SetResult(PayoutApproval.Result.InvalidState);
                    return;
                }
                var payoutBlob = payout.GetBlob(this._jsonSerializerSettings);
                if (payoutBlob.Revision != req.Revision)
                {
                    req.Completion.SetResult(PayoutApproval.Result.OldRevision);
                    return;
                }
                if (!PaymentMethodId.TryParse(payout.PaymentMethodId, out var paymentMethod))
                {
                    req.Completion.SetResult(PayoutApproval.Result.NotFound);
                    return;
                }
                payout.State = PayoutState.AwaitingPayment;

                if (paymentMethod.CryptoCode == payout.PullPaymentData.GetBlob().Currency)
                    req.Rate = 1.0m;
                var cryptoAmount = payoutBlob.Amount / req.Rate;
                var payoutHandler = _payoutHandlers.FindPayoutHandler(paymentMethod);
                if (payoutHandler is null)
                    throw new InvalidOperationException($"No payout handler for {paymentMethod}");
                var dest = await payoutHandler.ParseClaimDestination(paymentMethod, payoutBlob.Destination, false);
                decimal minimumCryptoAmount = await payoutHandler.GetMinimumPayoutAmount(paymentMethod, dest.destination);
                if (cryptoAmount < minimumCryptoAmount)
                {
                    req.Completion.TrySetResult(PayoutApproval.Result.TooLowAmount);
                    return;
                }
                payoutBlob.CryptoAmount = BTCPayServer.Extensions.RoundUp(cryptoAmount, _networkProvider.GetNetwork(paymentMethod.CryptoCode).Divisibility);
                payout.SetBlob(payoutBlob, _jsonSerializerSettings);
                await ctx.SaveChangesAsync();
                req.Completion.SetResult(PayoutApproval.Result.Ok);
            }
            catch (Exception ex)
            {
                req.Completion.TrySetException(ex);
            }
        }
        private async Task HandleMarkPaid(InternalPayoutPaidRequest req)
        {
            try
            {
                await using var ctx = _dbContextFactory.CreateContext();
                var payout = await ctx.Payouts.Include(p => p.PullPaymentData).Where(p => p.Id == req.Request.PayoutId).FirstOrDefaultAsync();
                if (payout is null)
                {
                    req.Completion.SetResult(PayoutPaidRequest.PayoutPaidResult.NotFound);
                    return;
                }
                if (payout.State != PayoutState.AwaitingPayment)
                {
                    req.Completion.SetResult(PayoutPaidRequest.PayoutPaidResult.InvalidState);
                    return;
                }
                if (req.Request.Proof != null)
                {
                    payout.SetProofBlob(req.Request.Proof);
                }
                payout.State = PayoutState.Completed;
                await ctx.SaveChangesAsync();
                req.Completion.SetResult(PayoutPaidRequest.PayoutPaidResult.Ok);
            }
            catch (Exception ex)
            {
                req.Completion.TrySetException(ex);
            }
        }

        private async Task HandleCreatePayout(PayoutRequest req)
        {
            try
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                await using var ctx = _dbContextFactory.CreateContext();
                var pp = await ctx.PullPayments.FindAsync(req.ClaimRequest.PullPaymentId);

                if (pp is null || pp.Archived)
                {
                    req.Completion.TrySetResult(new ClaimRequest.ClaimResponse(ClaimRequest.ClaimResult.Archived));
                    return;
                }
                if (pp.IsExpired(now))
                {
                    req.Completion.TrySetResult(new ClaimRequest.ClaimResponse(ClaimRequest.ClaimResult.Expired));
                    return;
                }
                if (!pp.HasStarted(now))
                {
                    req.Completion.TrySetResult(new ClaimRequest.ClaimResponse(ClaimRequest.ClaimResult.NotStarted));
                    return;
                }
                var ppBlob = pp.GetBlob();
                var payoutHandler =
                    _payoutHandlers.FindPayoutHandler(req.ClaimRequest.PaymentMethodId);
                if (!ppBlob.SupportedPaymentMethods.Contains(req.ClaimRequest.PaymentMethodId) || payoutHandler is null)
                {
                    req.Completion.TrySetResult(new ClaimRequest.ClaimResponse(ClaimRequest.ClaimResult.PaymentMethodNotSupported));
                    return;
                }

                if (req.ClaimRequest.Destination.Id != null)
                {
                    if (await ctx.Payouts.AnyAsync(data =>
                        data.Destination.Equals(req.ClaimRequest.Destination.Id) &&
                        data.State != PayoutState.Completed && data.State != PayoutState.Cancelled
                        ))
                    {

                        req.Completion.TrySetResult(new ClaimRequest.ClaimResponse(ClaimRequest.ClaimResult.Duplicate));
                        return;
                    }
                }

                if (req.ClaimRequest.Value <
                    await payoutHandler.GetMinimumPayoutAmount(req.ClaimRequest.PaymentMethodId,
                        req.ClaimRequest.Destination))
                {
                    req.Completion.TrySetResult(new ClaimRequest.ClaimResponse(ClaimRequest.ClaimResult.AmountTooLow));
                    return;
                }

                var payouts = (await ctx.Payouts.GetPayoutInPeriod(pp, now)
                                                .Where(p => p.State != PayoutState.Cancelled)
                                                .ToListAsync())
                              .Select(o => new
                              {
                                  Entity = o,
                                  Blob = o.GetBlob(_jsonSerializerSettings)
                              });
                var limit = ppBlob.Limit;
                var totalPayout = payouts.Select(p => p.Blob.Amount).Sum();
                var claimed = req.ClaimRequest.Value is decimal v ? v : limit - totalPayout;
                if (totalPayout + claimed > limit)
                {
                    req.Completion.TrySetResult(new ClaimRequest.ClaimResponse(ClaimRequest.ClaimResult.Overdraft));
                    return;
                }
                var payout = new PayoutData()
                {
                    Id = Encoders.Base58.EncodeData(RandomUtils.GetBytes(20)),
                    Date = now,
                    State = PayoutState.AwaitingApproval,
                    PullPaymentDataId = req.ClaimRequest.PullPaymentId,
                    PaymentMethodId = req.ClaimRequest.PaymentMethodId.ToString(),
                    Destination = req.ClaimRequest.Destination.Id
                };
                if (claimed < ppBlob.MinimumClaim || claimed == 0.0m)
                {
                    req.Completion.TrySetResult(new ClaimRequest.ClaimResponse(ClaimRequest.ClaimResult.AmountTooLow));
                    return;
                }
                var payoutBlob = new PayoutBlob()
                {
                    Amount = claimed,
                    Destination = req.ClaimRequest.Destination.ToString()
                };
                payout.SetBlob(payoutBlob, _jsonSerializerSettings);
                await ctx.Payouts.AddAsync(payout);
                try
                {
                    await payoutHandler.TrackClaim(req.ClaimRequest.PaymentMethodId, req.ClaimRequest.Destination);
                    await ctx.SaveChangesAsync();
                    req.Completion.TrySetResult(new ClaimRequest.ClaimResponse(ClaimRequest.ClaimResult.Ok, payout));
                    await _notificationSender.SendNotification(new StoreScope(pp.StoreId), new PayoutNotification()
                    {
                        StoreId = pp.StoreId,
                        Currency = ppBlob.Currency,
                        PaymentMethod = payout.PaymentMethodId,
                        PayoutId = pp.Id
                    });
                }
                catch (DbUpdateException)
                {
                    req.Completion.TrySetResult(new ClaimRequest.ClaimResponse(ClaimRequest.ClaimResult.Duplicate));
                }
            }
            catch (Exception ex)
            {
                req.Completion.TrySetException(ex);
            }
        }
        private async Task HandleCancel(CancelRequest cancel)
        {
            try
            {
                using var ctx = this._dbContextFactory.CreateContext();
                List<PayoutData> payouts = null;
                if (cancel.PullPaymentId != null)
                {
                    ctx.PullPayments.Attach(new Data.PullPaymentData() { Id = cancel.PullPaymentId, Archived = true })
                        .Property(o => o.Archived).IsModified = true;
                    payouts = await ctx.Payouts
                            .Where(p => p.PullPaymentDataId == cancel.PullPaymentId)
                            .ToListAsync();
                }
                else
                {
                    var payoutIds = cancel.PayoutIds.ToHashSet();
                    payouts = await ctx.Payouts
                            .Where(p => payoutIds.Contains(p.Id))
                            .ToListAsync();
                }

                foreach (var payout in payouts)
                {
                    if (payout.State != PayoutState.Completed && payout.State != PayoutState.InProgress)
                        payout.State = PayoutState.Cancelled;
                }
                await ctx.SaveChangesAsync();
                cancel.Completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                cancel.Completion.TrySetException(ex);
            }
        }
        public Task Cancel(CancelRequest cancelRequest)
        {
            CancellationToken.ThrowIfCancellationRequested();
            var cts = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancelRequest.Completion = cts;
            if (!_Channel.Writer.TryWrite(cancelRequest))
                throw new ObjectDisposedException(nameof(PullPaymentHostedService));
            return cts.Task;
        }

        public Task<ClaimRequest.ClaimResponse> Claim(ClaimRequest request)
        {
            CancellationToken.ThrowIfCancellationRequested();
            var cts = new TaskCompletionSource<ClaimRequest.ClaimResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_Channel.Writer.TryWrite(new PayoutRequest(cts, request)))
                throw new ObjectDisposedException(nameof(PullPaymentHostedService));
            return cts.Task;
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _Channel?.Writer.Complete();
            _subscriptions.Dispose();
            return base.StopAsync(cancellationToken);
        }

        public Task<PayoutPaidRequest.PayoutPaidResult> MarkPaid(PayoutPaidRequest request)
        {
            CancellationToken.ThrowIfCancellationRequested();
            var cts = new TaskCompletionSource<PayoutPaidRequest.PayoutPaidResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_Channel.Writer.TryWrite(new InternalPayoutPaidRequest(cts, request)))
                throw new ObjectDisposedException(nameof(PullPaymentHostedService));
            return cts.Task;
        }


        class InternalPayoutPaidRequest
        {
            public InternalPayoutPaidRequest(TaskCompletionSource<PayoutPaidRequest.PayoutPaidResult> completionSource, PayoutPaidRequest request)
            {
                ArgumentNullException.ThrowIfNull(request);
                ArgumentNullException.ThrowIfNull(completionSource);
                Completion = completionSource;
                Request = request;
            }
            public TaskCompletionSource<PayoutPaidRequest.PayoutPaidResult> Completion { get; set; }
            public PayoutPaidRequest Request { get; }
        }

    }

    public class PayoutPaidRequest
    {
        public enum PayoutPaidResult
        {
            Ok,
            NotFound,
            InvalidState
        }
        public string PayoutId { get; set; }
        public ManualPayoutProof Proof { get; set; }

        public static string GetErrorMessage(PayoutPaidResult result)
        {
            switch (result)
            {
                case PayoutPaidResult.NotFound:
                    return "The payout is not found";
                case PayoutPaidResult.Ok:
                    return "Ok";
                case PayoutPaidResult.InvalidState:
                    return "The payout is not in a state that can be marked as paid";
                default:
                    throw new NotSupportedException();
            }
        }

    }

    public class ClaimRequest
    {
        public static string GetErrorMessage(ClaimResult result)
        {
            switch (result)
            {
                case ClaimResult.Ok:
                    break;
                case ClaimResult.Duplicate:
                    return "This address is already used for another payout";
                case ClaimResult.Expired:
                    return "This pull payment is expired";
                case ClaimResult.NotStarted:
                    return "This pull payment has yet started";
                case ClaimResult.Archived:
                    return "This pull payment has been archived";
                case ClaimResult.Overdraft:
                    return "The payout amount overdraft the pull payment's limit";
                case ClaimResult.AmountTooLow:
                    return "The requested payout amount is too low";
                case ClaimResult.PaymentMethodNotSupported:
                    return "This payment method is not supported by the pull payment";
                default:
                    throw new NotSupportedException("Unsupported ClaimResult");
            }
            return null;
        }
        public class ClaimResponse
        {
            public ClaimResponse(ClaimResult result, PayoutData payoutData = null)
            {
                Result = result;
                PayoutData = payoutData;
            }
            public ClaimResult Result { get; set; }
            public PayoutData PayoutData { get; set; }
        }
        public enum ClaimResult
        {
            Ok,
            Duplicate,
            Expired,
            Archived,
            NotStarted,
            Overdraft,
            AmountTooLow,
            PaymentMethodNotSupported,
        }

        public PaymentMethodId PaymentMethodId { get; set; }
        public string PullPaymentId { get; set; }
        public decimal? Value { get; set; }
        public IClaimDestination Destination { get; set; }
    }

}
