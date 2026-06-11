#!/bin/sh

# Log to /tmp first (guaranteed to be writeable during boot)
LOG_FILE="/tmp/brew_boot.log"
echo "=== Homebrew Mount Task Started at $(date) ===" > "$LOG_FILE"

# 1. Wait for up to 60 seconds for the homes directory to become available
HOMES_DIR="/var/services/homes"
MOUNT_SOURCE=""

i=1
while [ $i -le 60 ]; do
  if [ -d "$HOMES_DIR" ]; then
    MOUNT_SOURCE=$(readlink -f "$HOMES_DIR")
    if [ -n "$MOUNT_SOURCE" ] && [ -d "$MOUNT_SOURCE" ]; then
      echo "Target homes directory found at: $MOUNT_SOURCE (after $i seconds)" >> "$LOG_FILE"
      break
    fi
  fi
  sleep 1
  i=$((i + 1))
done

if [ -z "$MOUNT_SOURCE" ]; then
  echo "ERROR: Homes directory was not found or mounted within 60 seconds." >> "$LOG_FILE"
  exit 1
fi

# 2. Ensure /home exists on the root partition
if [ ! -d /home ]; then
  mkdir /home
  echo "Created /home directory" >> "$LOG_FILE"
fi

# 3. Mount the directory
if ! grep -qs ' /home ' /proc/mounts; then
  mount -o bind "$MOUNT_SOURCE" /home 2>> "$LOG_FILE"
  if [ $? -eq 0 ]; then
    echo "Successfully mounted $MOUNT_SOURCE to /home" >> "$LOG_FILE"
  else
    echo "ERROR: Mount command failed." >> "$LOG_FILE"
    exit 1
  fi
else
  echo "/home is already mounted." >> "$LOG_FILE"
fi

# 4. Set permissions for /home
chown root:root /home
chmod 775 /home

# 5. Fix permissions for linuxbrew (if it exists)
if [ -d /home/linuxbrew ]; then
  chown root:root /home/linuxbrew
  chmod 775 /home/linuxbrew
  echo "Adjusted permissions for /home/linuxbrew" >> "$LOG_FILE"
fi

echo "=== Homebrew Mount Task Finished successfully at $(date) ===" >> "$LOG_FILE"

# Copy the log file to your homes folder once mounted
cp "$LOG_FILE" "$MOUNT_SOURCE/brew_boot_mount.log"
