<#
.SYNOPSIS
    Smoke test against a running Fudis.Payments instance: creates a pending order, signs a gateway
    notification the way a payment provider would, POSTs it and prints the resulting database state.

.EXAMPLE
    pwsh -File scripts/smoke-test.ps1 -BaseUrl http://localhost:5085
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:5085",
    [string]$Secret = "dev-only-secret-replace-me",
    [string]$Provider = "stripe",
    [string]$ProviderEventId = "",
    [decimal]$Amount = 55.00,
    [string]$Currency = "BRL"
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($ProviderEventId)) {
    $ProviderEventId = "evt_smoke_$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())"
}
$connectionString = "Server=(localdb)\MSSQLLocalDB;Database=FudisPayments;Trusted_Connection=True;MultipleActiveResultSets=True"

function New-SqlConnection {
    param([string]$ConnectionString)
    $connection = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
    $connection.Open()
    return $connection
}

# 1. Seed a pending order.
$connection = New-SqlConnection $connectionString
try {
    $command = $connection.CreateCommand()
    $command.CommandText = @"
INSERT INTO Orders (Status, DriverId, DeliveryFee, TotalAmount, Currency)
OUTPUT INSERTED.Id
VALUES ('PendingPayment', 42, 30.00, $Amount, '$Currency');
"@
    $orderId = [int]$command.ExecuteScalar()
    Write-Host "Seeded pending order #$orderId" -ForegroundColor Yellow
}
finally {
    $connection.Close()
}

# 2. Sign the notification exactly like the gateway does.
$paidAt = [DateTimeOffset]::UtcNow
$timestamp = [DateTimeOffset]::UtcNow
$invariant = [System.Globalization.CultureInfo]::InvariantCulture

$canonical = @(
    $Provider,
    $ProviderEventId,
    $orderId.ToString($invariant),
    $Amount.ToString("0.00", $invariant),
    $Currency.ToUpperInvariant(),
    $paidAt.UtcDateTime.ToString("O", $invariant),
    $timestamp.UtcDateTime.ToString("O", $invariant)
) -join "|"

$hmac = New-Object System.Security.Cryptography.HMACSHA256
$hmac.Key = [System.Text.Encoding]::UTF8.GetBytes($Secret)
$signature = (($hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($canonical))) | ForEach-Object { $_.ToString("x2") }) -join ""

# 3. POST it.
$body = @{
    provider        = $Provider
    providerEventId = $ProviderEventId
    orderId         = $orderId
    amount          = $Amount
    currency        = $Currency
    paidAt          = $paidAt.ToString("o")
} | ConvertTo-Json

$headers = @{
    "X-Fudis-Signature" = $signature
    "X-Fudis-Timestamp" = $timestamp.ToString("O")
}

Write-Host "POST $BaseUrl/api/payments/confirm-payment" -ForegroundColor Cyan
Write-Host "Canonical payload: $canonical" -ForegroundColor DarkGray

try {
    $response = Invoke-WebRequest -Uri "$BaseUrl/api/payments/confirm-payment" -Method Post -Headers $headers -ContentType "application/json" -Body $body -UseBasicParsing
    Write-Host "HTTP $($response.StatusCode): $($response.Content)" -ForegroundColor Green
}
catch {
    Write-Host "HTTP error: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.Exception.Response) {
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        Write-Host $reader.ReadToEnd() -ForegroundColor Red
    }
}

# 4. Same event again: must be idempotent.
try {
    $response = Invoke-WebRequest -Uri "$BaseUrl/api/payments/confirm-payment" -Method Post -Headers $headers -ContentType "application/json" -Body $body -UseBasicParsing
    Write-Host "Second delivery (same event): HTTP $($response.StatusCode): $($response.Content)" -ForegroundColor Green
}
catch {
    Write-Host "Second delivery failed: $($_.Exception.Message)" -ForegroundColor Red
}

# 5. Print the resulting state.
$connection = New-SqlConnection $connectionString
try {
    $command = $connection.CreateCommand()
    $command.CommandText = @"
SELECT o.Id, o.Status, (SELECT COUNT(*) FROM DriverEarnings e WHERE e.OrderId = o.Id) AS Earnings,
       (SELECT SUM(Amount) FROM DriverEarnings e WHERE e.OrderId = o.Id) AS EarningAmount,
       (SELECT COUNT(*) FROM PaymentInbox i WHERE i.OrderId = o.Id) AS Inbox,
       (SELECT COUNT(*) FROM RestaurantNotificationOutbox x WHERE x.OrderId = o.Id) AS Outbox
FROM Orders o WHERE o.Id = $orderId;
"@
    $reader = $command.ExecuteReader()
    $table = New-Object System.Data.DataTable
    $table.Load($reader)
    Write-Host "Resulting state:" -ForegroundColor Cyan
    $table | Format-Table -AutoSize | Out-String | Write-Host
}
finally {
    $connection.Close()
}
