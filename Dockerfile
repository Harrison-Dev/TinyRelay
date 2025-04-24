# Stage 1: Build the application
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build

# Set the working directory inside the container
WORKDIR /src

# Copy the solution file and project files
COPY TinyRelay.sln ./
COPY TinyRelay/TinyRelay.csproj TinyRelay/
COPY TinyRelay.Shared/TinyRelay.Shared.csproj TinyRelay.Shared/
COPY TinyRelay.Server/TinyRelay.Server.csproj TinyRelay.Server/
COPY TinyRelay.Client/TinyRelay.Client.csproj TinyRelay.Client/
COPY External/ External/

# Restore dependencies
RUN dotnet restore TinyRelay.sln

# Copy the entire source code
COPY TinyRelay/ TinyRelay/
COPY TinyRelay.Shared/ TinyRelay.Shared/
COPY TinyRelay.Server/ TinyRelay.Server/
COPY TinyRelay.Client/ TinyRelay.Client/

# Build the project
RUN dotnet publish TinyRelay.Server/TinyRelay.Server.csproj -c Release -o /app/publish

# Stage 2: Create the runtime image
FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine AS runtime

# Set the working directory inside the container
WORKDIR /app

# Copy the published output from the build stage
COPY --from=build /app/publish .

# Expose the port your application listens on
EXPOSE 9050

# Set the entry point for the application
ENTRYPOINT ["dotnet", "TinyRelay.Server.dll"]
