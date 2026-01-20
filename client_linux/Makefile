# VoiceTyper Linux Client Makefile

.PHONY: install run clean help install-udev check-deps

APP_NAME = VoiceTyper
VERSION = 1.3.1

# Default target
help:
	@echo "$(APP_NAME) Linux Client Build Commands"
	@echo "======================================"
	@echo ""
	@echo "  make install      - Install Python and system dependencies"
	@echo "  make install-udev - Install udev rules for input device access"
	@echo "  make check-deps   - Check if all dependencies are installed"
	@echo "  make run          - Run in development mode"
	@echo "  make clean        - Clean Python cache files"
	@echo "  make log          - View application logs"
	@echo "  make log-f        - Follow logs in real-time"
	@echo ""

# Install Python dependencies
install:
	@echo "Installing Python dependencies..."
	pip3 install -r requirements.txt
	@echo ""
	@echo "Python dependencies installed successfully!"
	@echo ""
	@echo "Next steps:"
	@echo "1. Install system dependencies (see README.md)"
	@echo "2. Run 'make install-udev' to configure permissions"
	@echo "3. Run 'make run' to start the application"

# Install udev rules
install-udev:
	@echo "Installing udev rules..."
	@sudo cp 99-voicetyper-input.rules /etc/udev/rules.d/
	@sudo udevadm control --reload-rules
	@echo ""
	@echo "Adding user to 'input' group..."
	@sudo usermod -aG input $USER
	@echo ""
	@echo "Udev rules installed successfully!"
	@echo ""
	@echo "IMPORTANT: Log out and log back in for group changes to take effect"

# Check dependencies
check-deps:
	@echo "Checking system dependencies..."
	@echo ""
	@command -v python3 >/dev/null 2>&1 && echo "✓ python3 is installed" || echo "✗ python3 is NOT installed"
	@command -v pip3 >/dev/null 2>&1 && echo "✓ pip3 is installed" || echo "✗ pip3 is NOT installed"
	@command -v wl-copy >/dev/null 2>&1 && echo "✓ wl-clipboard is installed" || echo "✗ wl-clipboard is NOT installed (install with: sudo apt install wl-clipboard)"
	@dpkg -l | grep -q gir1.2-gtk-4.0 && echo "✓ GTK4 Python bindings are installed" || echo "✗ GTK4 Python bindings are NOT installed (install with: sudo apt install gir1.2-gtk-4.0)"
	@echo ""
	@groups | grep -q input && echo "✓ User is in 'input' group" || echo "✗ User is NOT in 'input' group (run: make install-udev)"
	@echo ""

# Run the application
run:
	@echo "Starting $(APP_NAME)..."
	@python3 main.py

# Clean cache files
clean:
	@echo "Cleaning Python cache files..."
	@find . -type d -name "__pycache__" -exec rm -rf {} + 2>/dev/null || true
	@find . -type f -name "*.pyc" -delete 2>/dev/null || true
	@find . -type f -name "*.pyo" -delete 2>/dev/null || true
	@echo "Clean complete"

# View logs
log:
	@echo "Last 100 lines of application log:"
	@tail -100 ~/.config/voice_typer/app.log

# Follow logs
log-f:
	@echo "Following application log (Ctrl+C to stop)..."
	@tail -f ~/.config/voice_typer/app.log
