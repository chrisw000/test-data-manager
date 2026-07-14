# TDM host container image (W1-D1) — for CI agents without a .NET toolchain.
# Build:  docker build -t tdm:local --build-arg TDM_VERSION=0.1.0 .
# Usage:  docker run --rm -v $PWD:/work -w /work tdm:local validate --settings tdm.settings.json
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TDM_VERSION=0.1.0
WORKDIR /source
COPY Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet publish src/Tdm.Host/Tdm.Host.csproj -c Release -o /app -p:TdmVersion=${TDM_VERSION}

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "/app/tdm.dll"]
