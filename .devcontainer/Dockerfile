FROM mcr.microsoft.com/devcontainers/dotnet:2.2.3-11.0-preview@sha256:291f67fcd33196d4a4b84929902ea341aab511a26514aea09b52756c980452da

# The Kiota CLI (dotnet tool, used to generate API clients from OpenAPI specs) ships targeting
# a stable .NET runtime, not the preview SDK/runtime this repo is pinned to in global.json.
# Install it side by side so `dotnet kiota` works without affecting what the solution itself builds against.
RUN curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --channel 10.0 --runtime dotnet --install-dir /usr/share/dotnet \
    && rm /tmp/dotnet-install.sh
