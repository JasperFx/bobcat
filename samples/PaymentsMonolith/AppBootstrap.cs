namespace PaymentsMonolith;

public static class AppBootstrap
{
    public static void MapRoutes(WebApplication app)
    {
        // Accounts
        app.MapPost("/api/accounts", (CreateAccountRequest req) =>
        {
            var account = PaymentStore.CreateAccount(req);
            return Results.Created($"/api/accounts/{account.Id}", account);
        });

        app.MapGet("/api/accounts/{id:int}", (int id) =>
        {
            var account = PaymentStore.GetAccount(id);
            return account is not null ? Results.Ok(account) : Results.NotFound();
        });

        app.MapGet("/api/accounts/{id:int}/payments", (int id) =>
        {
            return PaymentStore.GetPaymentsForAccount(id);
        });

        // Payments
        app.MapPost("/api/payments", (MakePaymentRequest req) =>
        {
            var (payment, error) = PaymentStore.MakePayment(req);
            if (payment is null) return Results.BadRequest(new { error });
            return Results.Created($"/api/payments/{payment.Id}", payment);
        });

        app.MapGet("/api/payments/{id:int}", (int id) =>
        {
            var payment = PaymentStore.GetPayment(id);
            return payment is not null ? Results.Ok(payment) : Results.NotFound();
        });

        app.MapGet("/api/payments", (string? status) =>
        {
            if (status is not null)
                return Results.Ok(PaymentStore.GetPaymentsByStatus(status));
            return Results.Ok(PaymentStore.GetPaymentsByStatus("completed"));
        });

        app.MapPost("/api/payments/{id:int}/refund", (int id) =>
        {
            var payment = PaymentStore.RefundPayment(id);
            return payment is not null ? Results.Ok(payment) : Results.NotFound();
        });
    }
}
