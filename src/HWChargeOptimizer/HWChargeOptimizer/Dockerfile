# Dockerfile, use a .NET 10.0 runtime base image
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# copy the project file (relative to this Dockerfile) and restore
COPY ["HWChargeOptimizer.csproj", "./"]
RUN dotnet restore "HWChargeOptimizer.csproj"

# copy remaining source and build
COPY . .
WORKDIR "/src"
RUN dotnet build "HWChargeOptimizer.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "HWChargeOptimizer.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Permissions on volume directories
RUN mkdir -p /data/config /data/logs \
    && chown -R 1654:1654 /data \
    && chmod -R u+rwX,g+rwX /data \
    && find /data -type d -exec chmod g+s {} \;
USER 1654

# Declare the volume for persistent data
VOLUME ["/data"]

# Specify the entry point for the application with service argument
ENTRYPOINT ["dotnet", "hwco.dll"]
CMD ["--service"]