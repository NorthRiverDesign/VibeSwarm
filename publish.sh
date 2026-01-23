#!/bin/bash
# VibeSwarm Publish Script for Raspberry Pi
# This script publishes the application in Release mode

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_FILE="$SCRIPT_DIR/src/VibeSwarm.Web/VibeSwarm.Web.csproj"
OUTPUT_DIR="$SCRIPT_DIR/build"

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

echo -e "${BLUE}========================================${NC}"
echo -e "${BLUE}  VibeSwarm - Publishing for Pi${NC}"
echo -e "${BLUE}========================================${NC}"
echo ""

# Check if project file exists
if [ ! -f "$PROJECT_FILE" ]; then
    echo -e "${RED}Error: Project file not found at $PROJECT_FILE${NC}"
    exit 1
fi

# Clean previous build
if [ -d "$OUTPUT_DIR" ]; then
    echo -e "${YELLOW}Cleaning previous build...${NC}"
    rm -rf "$OUTPUT_DIR"
fi

# Publish the application
echo -e "${GREEN}Publishing application in Release mode...${NC}"
echo ""

dotnet publish "$PROJECT_FILE" \
    -c Release \
    -o "$OUTPUT_DIR" \
    --self-contained false

if [ $? -eq 0 ]; then
    echo ""
    echo -e "${GREEN}========================================${NC}"
    echo -e "${GREEN}  Build completed successfully!${NC}"
    echo -e "${GREEN}========================================${NC}"
    echo ""
    echo -e "Published to: ${BLUE}$OUTPUT_DIR${NC}"
    echo ""
    echo -e "To start the application, run:"
    echo -e "  ${BLUE}./start-vibeswarm.sh${NC}"
    echo ""
else
    echo ""
    echo -e "${RED}========================================${NC}"
    echo -e "${RED}  Build failed!${NC}"
    echo -e "${RED}========================================${NC}"
    exit 1
fi
