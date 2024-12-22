# Stage 1: Build the application
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build

WORKDIR /src

# Copy project files and restore dependencies
COPY ["TinyRelay.csproj", "./"]
COPY ["Shared/Shared.csproj", "Shared/"]

RUN dotnet restore "TinyRelay.csproj"

# Copy the remaining source code
COPY . .

# Publish the application in Release mode
RUN dotnet publish "TinyRelay.csproj" -c Release -o /app/publish

# Stage 2: Create the runtime image
FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine AS runtime

WORKDIR /app

# Copy the published output from the build stage
COPY --from=build /app/publish .

# Expose the port your application listens on (9050)
EXPOSE 9050

# Define the entry point for the container
ENTRYPOINT ["dotnet", "TinyRelay.dll"]
