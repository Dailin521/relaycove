$ErrorActionPreference = "SilentlyContinue"
$previewUrl = "http://127.0.0.1:5173/"

for ($attempt = 0; $attempt -lt 60; $attempt++) {
    $response = Invoke-WebRequest -Uri $previewUrl -UseBasicParsing -TimeoutSec 1
    if ($response.StatusCode -eq 200 -and $response.Content -match '<title>RelayCove</title>') {
        Start-Process $previewUrl
        exit 0
    }
    Start-Sleep -Milliseconds 250
}

exit 1
