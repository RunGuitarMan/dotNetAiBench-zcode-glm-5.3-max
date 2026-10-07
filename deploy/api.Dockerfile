FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Packages.props Directory.Build.props global.json NuGet.config ./
COPY src/Motiva.Domain/Motiva.Domain.csproj src/Motiva.Domain/packages.lock.json src/Motiva.Domain/
COPY src/Motiva.Application/Motiva.Application.csproj src/Motiva.Application/packages.lock.json src/Motiva.Application/
COPY src/Motiva.Infrastructure/Motiva.Infrastructure.csproj src/Motiva.Infrastructure/packages.lock.json src/Motiva.Infrastructure/
COPY src/Motiva.Api/Motiva.Api.csproj src/Motiva.Api/packages.lock.json src/Motiva.Api/
COPY src/Motiva.Worker/Motiva.Worker.csproj src/Motiva.Worker/packages.lock.json src/Motiva.Worker/
RUN dotnet restore src/Motiva.Api/Motiva.Api.csproj --locked-mode && dotnet restore src/Motiva.Worker/Motiva.Worker.csproj --locked-mode
COPY src src
RUN dotnet publish src/Motiva.Api/Motiva.Api.csproj -c Release -o /app/api --no-restore && \
    dotnet publish src/Motiva.Worker/Motiva.Worker.csproj -c Release -o /app/worker --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/api ./api
COPY --from=build /app/worker ./worker
ENV ASPNETCORE_URLS=http://+:8080
WORKDIR /app/api
ENTRYPOINT ["dotnet", "Motiva.Api.dll"]
