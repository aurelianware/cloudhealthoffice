#!/usr/bin/env python3
"""
SFTP Fetcher for Cloud Health Office

Downloads X12 EDI files from Clearinghouse SFTP server for processing.
Supports SSH key-based authentication, connection pooling, retry logic,
and Kubernetes Secret integration for credentials.

Usage:
    python fetch_sftp.py --host sftp.clearinghouse.example.com --folder /inbound --output /data/downloads
    python fetch_sftp.py --list-only  # List files without downloading

Environment Variables:
    SFTP_HOST: SFTP server hostname
    SFTP_PORT: SFTP server port (default: 22)
    SFTP_USERNAME: SFTP username
    SFTP_PASSWORD: SFTP password (if not using SSH key)
                   SECURITY: Use Azure Key Vault or Kubernetes Secrets in production.
                   Environment variables are not secure for sensitive credentials.
    SSH_KEY_PATH: Path to SSH private key file (preferred over password auth)
    LOG_LEVEL: Logging level (DEBUG, INFO, WARNING, ERROR)
"""

import argparse
import json
import logging
import os
import stat
import sys
import time
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import List, Optional, Dict, Any

import paramiko
from paramiko import SFTPClient, Transport


@dataclass
class SFTPConfig:
    """SFTP connection configuration"""
    host: str = ""
    port: int = 22
    username: str = ""
    password: str = ""
    ssh_key_path: str = ""
    known_hosts_path: str = ""
    timeout: int = 30
    max_retries: int = 3
    retry_delay: int = 5


@dataclass
class FileInfo:
    """Information about a remote file"""
    filename: str
    path: str
    size: int
    modified_time: datetime
    is_directory: bool = False


@dataclass
class FetchResult:
    """Result of fetch operation"""
    success: bool
    files_downloaded: List[str] = field(default_factory=list)
    files_failed: List[str] = field(default_factory=list)
    total_bytes: int = 0
    duration_seconds: float = 0.0
    errors: List[str] = field(default_factory=list)


class SFTPFetcher:
    """
    SFTP Client for downloading X12 EDI files
    
    Features:
    - SSH key and password authentication
    - Connection pooling with automatic reconnection
    - Retry logic with exponential backoff
    - File pattern matching
    - Batch download support
    """
    
    def __init__(self, config: SFTPConfig, log_level: str = "INFO"):
        """Initialize SFTP fetcher with configuration"""
        self.config = config
        self.logger = logging.getLogger("SFTPFetcher")
        self.logger.setLevel(getattr(logging, log_level.upper(), logging.INFO))
        
        if not self.logger.handlers:
            handler = logging.StreamHandler(sys.stdout)
            handler.setFormatter(logging.Formatter(
                '%(asctime)s - %(name)s - %(levelname)s - %(message)s'
            ))
            self.logger.addHandler(handler)
        
        self._transport: Optional[Transport] = None
        self._sftp: Optional[SFTPClient] = None
    
    def connect(self) -> bool:
        """Establish SFTP connection with retry logic"""
        for attempt in range(self.config.max_retries):
            try:
                self.logger.info(f"Connecting to {self.config.host}:{self.config.port} (attempt {attempt + 1})")
                
                # Create transport
                self._transport = Transport((self.config.host, self.config.port))
                
                # Authenticate
                if self.config.ssh_key_path and os.path.exists(self.config.ssh_key_path):
                    self.logger.debug("Using SSH key authentication")
                    private_key = self._load_private_key(self.config.ssh_key_path)
                    self._transport.connect(username=self.config.username, pkey=private_key)
                else:
                    self.logger.debug("Using password authentication")
                    self._transport.connect(
                        username=self.config.username,
                        password=self.config.password
                    )
                
                # Create SFTP client
                self._sftp = SFTPClient.from_transport(self._transport)
                self.logger.info("SFTP connection established successfully")
                return True
                
            except Exception as e:
                self.logger.warning(f"Connection attempt {attempt + 1} failed: {str(e)}")
                self.disconnect()
                
                if attempt < self.config.max_retries - 1:
                    delay = self.config.retry_delay * (2 ** attempt)  # Exponential backoff
                    self.logger.info(f"Retrying in {delay} seconds...")
                    time.sleep(delay)
        
        self.logger.error(f"Failed to connect after {self.config.max_retries} attempts")
        return False
    
    def disconnect(self):
        """Close SFTP connection"""
        if self._sftp:
            try:
                self._sftp.close()
            except Exception:
                pass
            self._sftp = None
        
        if self._transport:
            try:
                self._transport.close()
            except Exception:
                pass
            self._transport = None
        
        self.logger.debug("SFTP connection closed")
    
    def _load_private_key(self, key_path: str) -> paramiko.PKey:
        """Load SSH private key from file"""
        # Try different key types
        key_types = [
            (paramiko.RSAKey, "RSA"),
            (paramiko.Ed25519Key, "Ed25519"),
            (paramiko.ECDSAKey, "ECDSA"),
            (paramiko.DSSKey, "DSS")
        ]
        
        for key_class, key_name in key_types:
            try:
                return key_class.from_private_key_file(key_path)
            except Exception:
                continue
        
        raise ValueError(f"Unable to load private key from {key_path}")
    
    def list_files(self, remote_folder: str, pattern: str = "*.edi") -> List[FileInfo]:
        """List files in remote folder matching pattern"""
        if not self._sftp:
            raise RuntimeError("Not connected to SFTP server")
        
        self.logger.info(f"Listing files in {remote_folder} matching '{pattern}'")
        
        files = []
        try:
            for entry in self._sftp.listdir_attr(remote_folder):
                # Check if matches pattern (simple wildcard matching)
                if self._matches_pattern(entry.filename, pattern):
                    files.append(FileInfo(
                        filename=entry.filename,
                        path=f"{remote_folder}/{entry.filename}",
                        size=entry.st_size or 0,
                        modified_time=datetime.fromtimestamp(entry.st_mtime or 0),
                        is_directory=entry.st_mode is not None and stat.S_ISDIR(entry.st_mode)
                    ))
            
            self.logger.info(f"Found {len(files)} files matching pattern")
            
        except Exception as e:
            self.logger.error(f"Error listing files: {str(e)}")
            raise
        
        return files
    
    def _matches_pattern(self, filename: str, pattern: str) -> bool:
        """Simple wildcard pattern matching"""
        if pattern == "*":
            return True
        
        if pattern.startswith("*."):
            extension = pattern[1:]  # Get ".edi" from "*.edi"
            return filename.lower().endswith(extension.lower())
        
        if pattern.endswith("*"):
            prefix = pattern[:-1]
            return filename.lower().startswith(prefix.lower())
        
        return filename.lower() == pattern.lower()
    
    def download_file(self, remote_path: str, local_path: str) -> bool:
        """Download a single file"""
        if not self._sftp:
            raise RuntimeError("Not connected to SFTP server")
        
        self.logger.debug(f"Downloading {remote_path} to {local_path}")
        
        try:
            # Ensure local directory exists
            os.makedirs(os.path.dirname(local_path), exist_ok=True)
            
            # Download file
            self._sftp.get(remote_path, local_path)
            
            self.logger.info(f"Downloaded: {remote_path}")
            return True
            
        except Exception as e:
            self.logger.error(f"Failed to download {remote_path}: {str(e)}")
            return False
    
    def download_files(
        self,
        remote_folder: str,
        local_folder: str,
        pattern: str = "*.edi",
        delete_after_download: bool = False
    ) -> FetchResult:
        """Download all matching files from remote folder"""
        result = FetchResult(success=False)
        start_time = time.time()
        
        try:
            # List matching files
            files = self.list_files(remote_folder, pattern)
            
            for file_info in files:
                if file_info.is_directory:
                    continue
                
                local_path = os.path.join(local_folder, file_info.filename)
                
                if self.download_file(file_info.path, local_path):
                    result.files_downloaded.append(file_info.filename)
                    result.total_bytes += file_info.size
                    
                    # Delete from remote if requested
                    if delete_after_download:
                        try:
                            self._sftp.remove(file_info.path)
                            self.logger.info(f"Deleted remote file: {file_info.path}")
                        except Exception as e:
                            self.logger.warning(f"Failed to delete {file_info.path}: {str(e)}")
                else:
                    result.files_failed.append(file_info.filename)
            
            result.success = len(result.files_failed) == 0
            
        except Exception as e:
            result.errors.append(str(e))
            self.logger.error(f"Download operation failed: {str(e)}")
        
        result.duration_seconds = time.time() - start_time
        return result
    
    def delete_file(self, remote_path: str) -> bool:
        """Delete a file from remote server"""
        if not self._sftp:
            raise RuntimeError("Not connected to SFTP server")
        
        try:
            self._sftp.remove(remote_path)
            self.logger.info(f"Deleted remote file: {remote_path}")
            return True
        except Exception as e:
            self.logger.error(f"Failed to delete {remote_path}: {str(e)}")
            return False


def load_config_from_env() -> SFTPConfig:
    """Load SFTP configuration from environment variables"""
    return SFTPConfig(
        host=os.environ.get("SFTP_HOST", ""),
        port=int(os.environ.get("SFTP_PORT", "22")),
        username=os.environ.get("SFTP_USERNAME", ""),
        password=os.environ.get("SFTP_PASSWORD", ""),
        ssh_key_path=os.environ.get("SSH_KEY_PATH", "/secrets/ssh-key"),
        timeout=int(os.environ.get("SFTP_TIMEOUT", "30")),
        max_retries=int(os.environ.get("SFTP_MAX_RETRIES", "3")),
        retry_delay=int(os.environ.get("SFTP_RETRY_DELAY", "5"))
    )


def main():
    """Main entry point for SFTP fetcher"""
    parser = argparse.ArgumentParser(
        description="Download X12 EDI files from Clearinghouse SFTP server"
    )
    
    # Connection options
    parser.add_argument("--host", default=os.environ.get("SFTP_HOST", ""), help="SFTP server hostname")
    parser.add_argument("--port", type=int, default=int(os.environ.get("SFTP_PORT", "22")), help="SFTP server port")
    parser.add_argument("--username", default=os.environ.get("SFTP_USERNAME", ""), help="SFTP username")
    parser.add_argument("--password", default=os.environ.get("SFTP_PASSWORD", ""), help="SFTP password")
    parser.add_argument("--ssh-key", default=os.environ.get("SSH_KEY_PATH", ""), help="Path to SSH private key")
    
    # Operation options
    parser.add_argument("-f", "--folder", default="/inbound", help="Remote folder to fetch from")
    parser.add_argument("-o", "--output", default="/data/output", help="Local output folder")
    parser.add_argument("-p", "--pattern", default="*.edi", help="File pattern to match")
    parser.add_argument("--delete-after", action="store_true", help="Delete files after successful download")
    parser.add_argument("--list-only", action="store_true", help="List files without downloading")
    
    # Output options
    parser.add_argument("--json", action="store_true", help="Output results as JSON")
    parser.add_argument("-l", "--log-level", default=os.environ.get("LOG_LEVEL", "INFO"),
                       choices=["DEBUG", "INFO", "WARNING", "ERROR"], help="Logging level")
    
    args = parser.parse_args()
    
    # Build configuration
    config = SFTPConfig(
        host=args.host,
        port=args.port,
        username=args.username,
        password=args.password,
        ssh_key_path=args.ssh_key
    )
    
    # Validate required fields
    if not config.host:
        print("Error: SFTP host is required (--host or SFTP_HOST env var)", file=sys.stderr)
        sys.exit(1)
    
    if not config.username:
        print("Error: SFTP username is required (--username or SFTP_USERNAME env var)", file=sys.stderr)
        sys.exit(1)
    
    # Initialize fetcher
    fetcher = SFTPFetcher(config, log_level=args.log_level)
    
    try:
        # Connect to SFTP server
        if not fetcher.connect():
            print("Error: Failed to connect to SFTP server", file=sys.stderr)
            sys.exit(1)
        
        if args.list_only:
            # List files only
            files = fetcher.list_files(args.folder, args.pattern)
            
            if args.json:
                output = {
                    "files": [
                        {
                            "filename": f.filename,
                            "path": f.path,
                            "size": f.size,
                            "modified_time": f.modified_time.isoformat()
                        }
                        for f in files
                    ],
                    "count": len(files)
                }
                print(json.dumps(output, indent=2))
            else:
                print(f"Found {len(files)} files in {args.folder}:")
                for f in files:
                    print(f"  {f.filename} ({f.size} bytes, modified {f.modified_time})")
        else:
            # Download files
            result = fetcher.download_files(
                remote_folder=args.folder,
                local_folder=args.output,
                pattern=args.pattern,
                delete_after_download=args.delete_after
            )
            
            if args.json:
                output = {
                    "success": result.success,
                    "files_downloaded": result.files_downloaded,
                    "files_failed": result.files_failed,
                    "total_bytes": result.total_bytes,
                    "duration_seconds": result.duration_seconds,
                    "errors": result.errors
                }
                print(json.dumps(output, indent=2))
            else:
                print(f"Download complete: {len(result.files_downloaded)} files, "
                      f"{result.total_bytes} bytes in {result.duration_seconds:.2f}s")
                if result.files_failed:
                    print(f"Failed: {', '.join(result.files_failed)}")
            
            if not result.success:
                sys.exit(1)
    
    finally:
        fetcher.disconnect()
    
    sys.exit(0)


if __name__ == "__main__":
    main()
