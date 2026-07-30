FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["SocialGraph.Api/SocialGraph.Api.csproj", "SocialGraph.Api/"]
RUN dotnet restore "SocialGraph.Api/SocialGraph.Api.csproj"

COPY . .
RUN dotnet publish "SocialGraph.Api/SocialGraph.Api.csproj" \
    --configuration "$BUILD_CONFIGURATION" \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final

WORKDIR /app

# Docker Compose probes /health/ready from inside the container. Npgsql loads
# GSSAPI dynamically even when password authentication is used.
RUN apk add --no-cache curl krb5-libs

ENV ASPNETCORE_HTTP_PORTS=1002
EXPOSE 1002

COPY --from=build /app/publish .

USER $APP_UID
ENTRYPOINT ["dotnet", "SocialGraph.Api.dll"]
