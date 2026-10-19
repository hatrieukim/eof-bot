# Two-stage build: the sdk stage only compiles, the final image only has the ASP.NET runtime.
# Building on an ARM Mac for an x86_64 server needs --platform linux/amd64.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# Copy the csproj and restore first: code-only changes reuse the cached restore layer
COPY EofBot.csproj .
RUN dotnet restore EofBot.csproj
COPY . .
RUN dotnet publish EofBot.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
# Documents and SQLite live in /app/data, mounted from the host (not in the image). .env is passed at runtime too
ENTRYPOINT ["dotnet", "EofBot.dll"]
