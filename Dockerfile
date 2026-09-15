FROM mcr.microsoft.com/dotnet/sdk:10.0.300 AS build
WORKDIR /source
COPY global.json FlightDeals.slnx ./
COPY src/FlightDeals/FlightDeals.csproj src/FlightDeals/
RUN dotnet restore src/FlightDeals/FlightDeals.csproj
COPY src/FlightDeals/ src/FlightDeals/
RUN dotnet publish src/FlightDeals/FlightDeals.csproj -c Release --no-restore -o /publish /p:UseAppHost=false

# Optional offline-provider test image; never part of the production runtime.
FROM build AS tests
COPY tests/ tests/
RUN dotnet restore FlightDeals.slnx
CMD ["dotnet", "test", "FlightDeals.slnx", "--no-restore", "--nologo"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0.8 AS runtime
WORKDIR /app
COPY --from=build /publish ./
USER app
ENTRYPOINT ["dotnet", "FlightDeals.dll"]
