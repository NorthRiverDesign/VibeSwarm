#!/bin/bash
# VibeSwarm Startup Script for Raspberry Pi
# This script starts the VibeSwarm web application in production mode

# Set the directory where the published app is located
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD_DIR="$SCRIPT_DIR/build"
APP_DLL="$BUILD_DIR/VibeSwarm.Web.dll"
APP_EXECUTABLE="$BUILD_DIR/VibeSwarm.Web"

# Set environment variables
export ASPNETCORE_ENVIRONMENT=Production
export ASPNETCORE_URLS="http://0.0.0.0:5000,https://0.0.0.0:5001"

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
RED='\033[0;31m'
NC='\033[0m' # No Color

echo -e "${BLUE}========================================${NC}"
echo -e "${BLUE}  VibeSwarm - Raspberry Pi Startup${NC}"
echo -e "${BLUE}========================================${NC}"
echo ""

# Check if build directory exists
if [ ! -d "$BUILD_DIR" ]; then
    echo -e "${RED}Error: Build directory not found at $BUILD_DIR${NC}"
    echo "Please run: ./publish.sh"
    exit 1
fi

# Check if the executable exists
if [ ! -f "$APP_EXECUTABLE" ]; then
    echo -e "${RED}Error: Application executable not found at $APP_EXECUTABLE${NC}"
    echo "Please run: ./publish.sh"
    exit 1
fi

# Make the executable runnable
chmod +x "$APP_EXECUTABLE"

# Get local IP address
LOCAL_IP=$(hostname -I | awk '{print $1}')

echo -e "${GREEN}Starting VibeSwarm...${NC}"
echo -e "Environment: ${GREEN}Production${NC}"
echo -e "Listening on: ${GREEN}http://0.0.0.0:5000${NC}"
echo -e "Listening on: ${GREEN}https://0.0.0.0:5001${NC}"
echo ""
echo -e "Access the application at:"
echo -e "  ${BLUE}http://localhost:5000${NC} (from this device)"
echo -e "  ${BLUE}http://$LOCAL_IP:5000${NC} (from other devices on your network)"
echo -e "  ${BLUE}https://localhost:5001${NC} (from this device)"
echo -e "  ${BLUE}https://$LOCAL_IP:5001${NC} (from other devices on your network)"
echo ""
echo -e "${GREEN}Press Ctrl+C to stop the server${NC}"
echo ""

# Start the application
cd "$BUILD_DIR"
exec ./VibeSwarm.Web
