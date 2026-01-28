#!/usr/bin/env python3
"""
Kafka Publisher for Cloud Health Office

Publishes X12 EDI processing events to Apache Kafka topics.
Supports SASL/SSL authentication, configurable topic routing,
JSON message serialization, and delivery confirmation.

Topics:
- edi-raw-files: Raw EDI file events
- attachments-in: Processed 275 attachment events
- edi-278: Processed 278 authorization events
- rfai-requests: 277 generation requests
- edi-277-outbound: Generated 277 responses
- dead-letter-queue: Failed processing events

Usage:
    python publish.py --bootstrap-servers kafka:9092 --topic attachments-in --message '{"claimNumber": "CLM123"}'
    python publish.py --input metadata.json --topic edi-278

Environment Variables:
    KAFKA_BOOTSTRAP_SERVERS: Kafka broker addresses
    KAFKA_SECURITY_PROTOCOL: PLAINTEXT, SSL, SASL_PLAINTEXT, SASL_SSL
    KAFKA_SASL_MECHANISM: PLAIN, SCRAM-SHA-256, SCRAM-SHA-512
    KAFKA_SASL_USERNAME: SASL username
    KAFKA_SASL_PASSWORD: SASL password
                         SECURITY: Use Azure Key Vault or Kubernetes Secrets in production.
                         Environment variables are not secure for sensitive credentials.
    LOG_LEVEL: Logging level
"""

import argparse
import json
import logging
import os
import sys
import time
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Dict, List, Optional, Any, Callable

from kafka import KafkaProducer
from kafka.errors import KafkaError, NoBrokersAvailable


@dataclass
class KafkaConfig:
    """Kafka connection configuration"""
    bootstrap_servers: str = ""
    security_protocol: str = "PLAINTEXT"
    sasl_mechanism: str = ""
    sasl_username: str = ""
    sasl_password: str = ""
    ssl_cafile: str = ""
    ssl_certfile: str = ""
    ssl_keyfile: str = ""
    acks: str = "all"  # "all" for strongest durability guarantee
    retries: int = 3
    retry_backoff_ms: int = 1000
    max_block_ms: int = 60000
    compression_type: str = "lz4"


@dataclass
class PublishResult:
    """Result of publish operation"""
    success: bool
    topic: str
    partition: int = -1
    offset: int = -1
    key: str = ""
    timestamp: int = 0
    error: str = ""


@dataclass
class BatchPublishResult:
    """Result of batch publish operation"""
    success: bool
    total_messages: int = 0
    successful: int = 0
    failed: int = 0
    results: List[PublishResult] = field(default_factory=list)
    duration_seconds: float = 0.0


class KafkaPublisher:
    """
    Kafka Publisher for X12 EDI events
    
    Features:
    - SASL/SSL authentication
    - JSON serialization
    - Delivery confirmation
    - Dead-letter queue routing
    - Configurable topic mapping
    """
    
    # Default topic mappings for EDI transaction types
    TOPIC_MAPPINGS = {
        "275": "attachments-in",
        "277": "edi-277-outbound",
        "278": "edi-278",
        "raw": "edi-raw-files",
        "rfai": "rfai-requests",
        "dlq": "dead-letter-queue"
    }
    
    def __init__(self, config: KafkaConfig, log_level: str = "INFO"):
        """Initialize Kafka publisher with configuration"""
        self.config = config
        self.logger = logging.getLogger("KafkaPublisher")
        self.logger.setLevel(getattr(logging, log_level.upper(), logging.INFO))
        
        if not self.logger.handlers:
            handler = logging.StreamHandler(sys.stdout)
            handler.setFormatter(logging.Formatter(
                '%(asctime)s - %(name)s - %(levelname)s - %(message)s'
            ))
            self.logger.addHandler(handler)
        
        self._producer: Optional[KafkaProducer] = None
    
    def connect(self) -> bool:
        """Initialize Kafka producer"""
        self.logger.info(f"Connecting to Kafka: {self.config.bootstrap_servers}")
        
        try:
            # Build producer configuration
            producer_config = {
                "bootstrap_servers": self.config.bootstrap_servers.split(","),
                "acks": self.config.acks,
                "retries": self.config.retries,
                "retry_backoff_ms": self.config.retry_backoff_ms,
                "max_block_ms": self.config.max_block_ms,
                "compression_type": self.config.compression_type,
                "value_serializer": lambda v: json.dumps(v).encode('utf-8'),
                "key_serializer": lambda k: k.encode('utf-8') if k else None
            }
            
            # Add security configuration
            if self.config.security_protocol != "PLAINTEXT":
                producer_config["security_protocol"] = self.config.security_protocol
                
                if "SASL" in self.config.security_protocol:
                    producer_config["sasl_mechanism"] = self.config.sasl_mechanism
                    producer_config["sasl_plain_username"] = self.config.sasl_username
                    producer_config["sasl_plain_password"] = self.config.sasl_password
                
                if "SSL" in self.config.security_protocol:
                    if self.config.ssl_cafile:
                        producer_config["ssl_cafile"] = self.config.ssl_cafile
                    if self.config.ssl_certfile:
                        producer_config["ssl_certfile"] = self.config.ssl_certfile
                    if self.config.ssl_keyfile:
                        producer_config["ssl_keyfile"] = self.config.ssl_keyfile
            
            self._producer = KafkaProducer(**producer_config)
            self.logger.info("Kafka producer initialized successfully")
            return True
            
        except NoBrokersAvailable as e:
            self.logger.error(f"No Kafka brokers available: {str(e)}")
            return False
        except Exception as e:
            self.logger.error(f"Failed to initialize Kafka producer: {str(e)}")
            return False
    
    def disconnect(self):
        """Close Kafka producer"""
        if self._producer:
            try:
                self._producer.flush(timeout=10)
                self._producer.close(timeout=10)
            except Exception as e:
                self.logger.warning(f"Error closing producer: {str(e)}")
            finally:
                self._producer = None
        
        self.logger.debug("Kafka producer closed")
    
    def publish(
        self,
        topic: str,
        message: Dict[str, Any],
        key: Optional[str] = None,
        headers: Optional[Dict[str, str]] = None,
        partition: Optional[int] = None
    ) -> PublishResult:
        """Publish a single message to Kafka topic"""
        if not self._producer:
            return PublishResult(
                success=False,
                topic=topic,
                error="Producer not initialized"
            )
        
        result = PublishResult(topic=topic, key=key or "")
        
        try:
            # Add standard headers
            kafka_headers = []
            if headers:
                kafka_headers = [(k, v.encode('utf-8')) for k, v in headers.items()]
            
            # Add trace headers for HIPAA compliance
            kafka_headers.extend([
                ("x-correlation-id", str(uuid.uuid4()).encode('utf-8')),
                ("x-timestamp", datetime.now(timezone.utc).isoformat().encode('utf-8')),
                ("x-source", "cloudhealthoffice".encode('utf-8'))
            ])
            
            # Send message
            future = self._producer.send(
                topic,
                value=message,
                key=key,
                headers=kafka_headers,
                partition=partition
            )
            
            # Wait for confirmation
            record_metadata = future.get(timeout=30)
            
            result.success = True
            result.partition = record_metadata.partition
            result.offset = record_metadata.offset
            result.timestamp = record_metadata.timestamp or int(time.time() * 1000)
            
            self.logger.info(f"Published to {topic}[{result.partition}] offset={result.offset}")
            
        except KafkaError as e:
            result.success = False
            result.error = str(e)
            self.logger.error(f"Failed to publish to {topic}: {str(e)}")
        except Exception as e:
            result.success = False
            result.error = str(e)
            self.logger.error(f"Unexpected error publishing to {topic}: {str(e)}")
        
        return result
    
    def publish_batch(
        self,
        topic: str,
        messages: List[Dict[str, Any]],
        key_extractor: Optional[Callable[[Dict], str]] = None
    ) -> BatchPublishResult:
        """Publish multiple messages to a topic"""
        result = BatchPublishResult(
            success=False,
            total_messages=len(messages)
        )
        start_time = time.time()
        
        for message in messages:
            key = key_extractor(message) if key_extractor else None
            pub_result = self.publish(topic, message, key=key)
            result.results.append(pub_result)
            
            if pub_result.success:
                result.successful += 1
            else:
                result.failed += 1
        
        # Flush to ensure all messages are sent
        if self._producer:
            self._producer.flush(timeout=30)
        
        result.success = result.failed == 0
        result.duration_seconds = time.time() - start_time
        
        return result
    
    def publish_to_dlq(
        self,
        original_topic: str,
        original_message: Dict[str, Any],
        error_message: str,
        error_code: str = "PROCESSING_ERROR"
    ) -> PublishResult:
        """Publish failed message to dead-letter queue"""
        dlq_message = {
            "original_topic": original_topic,
            "original_message": original_message,
            "error_code": error_code,
            "error_message": error_message,
            "failed_at": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            "retry_count": 0
        }
        
        headers = {
            "x-original-topic": original_topic,
            "x-error-code": error_code
        }
        
        return self.publish(
            self.TOPIC_MAPPINGS["dlq"],
            dlq_message,
            headers=headers
        )
    
    def get_topic_for_transaction(self, transaction_type: str) -> str:
        """Get the appropriate topic for a transaction type"""
        return self.TOPIC_MAPPINGS.get(transaction_type, "edi-raw-files")


def load_config_from_env() -> KafkaConfig:
    """Load Kafka configuration from environment variables"""
    return KafkaConfig(
        bootstrap_servers=os.environ.get("KAFKA_BOOTSTRAP_SERVERS", ""),
        security_protocol=os.environ.get("KAFKA_SECURITY_PROTOCOL", "PLAINTEXT"),
        sasl_mechanism=os.environ.get("KAFKA_SASL_MECHANISM", ""),
        sasl_username=os.environ.get("KAFKA_SASL_USERNAME", ""),
        sasl_password=os.environ.get("KAFKA_SASL_PASSWORD", ""),
        ssl_cafile=os.environ.get("KAFKA_SSL_CAFILE", ""),
        ssl_certfile=os.environ.get("KAFKA_SSL_CERTFILE", ""),
        ssl_keyfile=os.environ.get("KAFKA_SSL_KEYFILE", ""),
        compression_type=os.environ.get("KAFKA_COMPRESSION_TYPE", "lz4")
    )


def main():
    """Main entry point for Kafka publisher"""
    parser = argparse.ArgumentParser(
        description="Publish X12 EDI events to Apache Kafka"
    )
    
    # Connection options
    parser.add_argument("--bootstrap-servers", 
                       default=os.environ.get("KAFKA_BOOTSTRAP_SERVERS", ""),
                       help="Kafka broker addresses (comma-separated)")
    parser.add_argument("--security-protocol",
                       default=os.environ.get("KAFKA_SECURITY_PROTOCOL", "PLAINTEXT"),
                       choices=["PLAINTEXT", "SSL", "SASL_PLAINTEXT", "SASL_SSL"],
                       help="Security protocol")
    parser.add_argument("--sasl-mechanism",
                       default=os.environ.get("KAFKA_SASL_MECHANISM", ""),
                       help="SASL mechanism (PLAIN, SCRAM-SHA-256, SCRAM-SHA-512)")
    parser.add_argument("--sasl-username",
                       default=os.environ.get("KAFKA_SASL_USERNAME", ""),
                       help="SASL username")
    parser.add_argument("--sasl-password",
                       default=os.environ.get("KAFKA_SASL_PASSWORD", ""),
                       help="SASL password")
    
    # Message options
    parser.add_argument("-t", "--topic", required=True, help="Target Kafka topic")
    parser.add_argument("-m", "--message", help="JSON message to publish")
    parser.add_argument("-i", "--input", help="Input JSON file to publish")
    parser.add_argument("-k", "--key", help="Message key")
    parser.add_argument("--stdin", action="store_true", help="Read message from stdin")
    
    # Output options
    parser.add_argument("--json", action="store_true", help="Output result as JSON")
    parser.add_argument("-l", "--log-level",
                       default=os.environ.get("LOG_LEVEL", "INFO"),
                       choices=["DEBUG", "INFO", "WARNING", "ERROR"],
                       help="Logging level")
    
    args = parser.parse_args()
    
    # Validate required fields
    if not args.bootstrap_servers:
        print("Error: Kafka bootstrap servers required (--bootstrap-servers or KAFKA_BOOTSTRAP_SERVERS)", 
              file=sys.stderr)
        sys.exit(1)
    
    if not args.message and not args.input and not args.stdin:
        print("Error: Must specify --message, --input, or --stdin", file=sys.stderr)
        sys.exit(1)
    
    # Build configuration
    config = KafkaConfig(
        bootstrap_servers=args.bootstrap_servers,
        security_protocol=args.security_protocol,
        sasl_mechanism=args.sasl_mechanism,
        sasl_username=args.sasl_username,
        sasl_password=args.sasl_password
    )
    
    # Initialize publisher
    publisher = KafkaPublisher(config, log_level=args.log_level)
    
    try:
        # Connect to Kafka
        if not publisher.connect():
            print("Error: Failed to connect to Kafka", file=sys.stderr)
            sys.exit(1)
        
        # Parse message
        if args.stdin:
            message = json.load(sys.stdin)
        elif args.input:
            with open(args.input, 'r') as f:
                message = json.load(f)
        else:
            message = json.loads(args.message)
        
        # Publish message
        result = publisher.publish(args.topic, message, key=args.key)
        
        # Output result
        if args.json:
            output = {
                "success": result.success,
                "topic": result.topic,
                "partition": result.partition,
                "offset": result.offset,
                "key": result.key,
                "timestamp": result.timestamp,
                "error": result.error
            }
            print(json.dumps(output, indent=2))
        else:
            if result.success:
                print(f"Published to {result.topic}[{result.partition}] offset={result.offset}")
            else:
                print(f"Failed: {result.error}", file=sys.stderr)
        
        if not result.success:
            sys.exit(1)
    
    except json.JSONDecodeError as e:
        print(f"Error: Invalid JSON: {str(e)}", file=sys.stderr)
        sys.exit(1)
    except FileNotFoundError:
        print(f"Error: Input file not found: {args.input}", file=sys.stderr)
        sys.exit(1)
    finally:
        publisher.disconnect()
    
    sys.exit(0)


if __name__ == "__main__":
    main()
