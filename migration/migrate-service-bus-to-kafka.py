#!/usr/bin/env python3
"""
Service Bus to Kafka Migration Script

Exports messages from Azure Service Bus topics and replays them to Kafka topics.
Used for migrating historical data during the Logic Apps to Argo migration.

Usage:
    python migrate-service-bus-to-kafka.py --topic attachments-in --limit 1000
    python migrate-service-bus-to-kafka.py --all-topics --verify
"""

import argparse
import json
import logging
import os
import sys
from dataclasses import dataclass, field
from datetime import datetime
from typing import List, Dict, Optional, Any

# Azure Service Bus SDK
try:
    from azure.servicebus import ServiceBusClient, ServiceBusReceiveMode
except ImportError:
    print("Azure Service Bus SDK not installed. Run: pip install azure-servicebus")
    sys.exit(1)

# Kafka Python SDK
try:
    from kafka import KafkaProducer
    from kafka.errors import KafkaError
except ImportError:
    print("Kafka Python SDK not installed. Run: pip install kafka-python")
    sys.exit(1)


@dataclass
class MigrationConfig:
    """Configuration for Service Bus to Kafka migration"""
    # Azure Service Bus
    servicebus_connection_string: str = ""
    servicebus_namespace: str = ""
    
    # Kafka
    kafka_bootstrap_servers: str = ""
    kafka_security_protocol: str = "PLAINTEXT"
    kafka_sasl_mechanism: str = ""
    kafka_sasl_username: str = ""
    kafka_sasl_password: str = ""
    
    # Topic mapping (Service Bus topic -> Kafka topic)
    topic_mapping: Dict[str, str] = field(default_factory=lambda: {
        "attachments-in": "attachments-in",
        "edi-278": "edi-278",
        "rfai-requests": "rfai-requests",
        "dead-letter": "dead-letter-queue"
    })


@dataclass
class MigrationResult:
    """Result of migration operation"""
    success: bool = False
    messages_exported: int = 0
    messages_imported: int = 0
    messages_failed: int = 0
    errors: List[str] = field(default_factory=list)
    duration_seconds: float = 0.0


class ServiceBusToKafkaMigrator:
    """
    Migrates messages from Azure Service Bus to Apache Kafka
    """
    
    def __init__(self, config: MigrationConfig, log_level: str = "INFO"):
        self.config = config
        self.logger = logging.getLogger("ServiceBusToKafkaMigrator")
        self.logger.setLevel(getattr(logging, log_level.upper(), logging.INFO))
        
        if not self.logger.handlers:
            handler = logging.StreamHandler(sys.stdout)
            handler.setFormatter(logging.Formatter(
                '%(asctime)s - %(name)s - %(levelname)s - %(message)s'
            ))
            self.logger.addHandler(handler)
        
        self._sb_client: Optional[ServiceBusClient] = None
        self._kafka_producer: Optional[KafkaProducer] = None
    
    def connect_servicebus(self) -> bool:
        """Connect to Azure Service Bus"""
        try:
            self._sb_client = ServiceBusClient.from_connection_string(
                self.config.servicebus_connection_string
            )
            self.logger.info("Connected to Azure Service Bus")
            return True
        except Exception as e:
            self.logger.error(f"Failed to connect to Service Bus: {e}")
            return False
    
    def connect_kafka(self) -> bool:
        """Connect to Kafka"""
        try:
            producer_config = {
                "bootstrap_servers": self.config.kafka_bootstrap_servers.split(","),
                "value_serializer": lambda v: json.dumps(v).encode('utf-8'),
                "key_serializer": lambda k: k.encode('utf-8') if k else None,
                "acks": "all",
                "retries": 3
            }
            
            if self.config.kafka_security_protocol != "PLAINTEXT":
                producer_config["security_protocol"] = self.config.kafka_security_protocol
                
                if "SASL" in self.config.kafka_security_protocol:
                    producer_config["sasl_mechanism"] = self.config.kafka_sasl_mechanism
                    producer_config["sasl_plain_username"] = self.config.kafka_sasl_username
                    producer_config["sasl_plain_password"] = self.config.kafka_sasl_password
            
            self._kafka_producer = KafkaProducer(**producer_config)
            self.logger.info("Connected to Kafka")
            return True
        except Exception as e:
            self.logger.error(f"Failed to connect to Kafka: {e}")
            return False
    
    def disconnect(self):
        """Disconnect from all services"""
        if self._sb_client:
            self._sb_client.close()
            self._sb_client = None
        
        if self._kafka_producer:
            self._kafka_producer.close()
            self._kafka_producer = None
    
    def migrate_topic(
        self,
        servicebus_topic: str,
        subscription: str = "migration-sub",
        limit: int = 0,
        verify: bool = False
    ) -> MigrationResult:
        """Migrate messages from a Service Bus topic to Kafka"""
        result = MigrationResult()
        start_time = datetime.now()
        
        kafka_topic = self.config.topic_mapping.get(servicebus_topic, servicebus_topic)
        self.logger.info(f"Migrating {servicebus_topic} -> {kafka_topic}")
        
        if not self._sb_client or not self._kafka_producer:
            result.errors.append("Not connected to services")
            return result
        
        try:
            # Create receiver for the topic subscription
            receiver = self._sb_client.get_subscription_receiver(
                topic_name=servicebus_topic,
                subscription_name=subscription,
                receive_mode=ServiceBusReceiveMode.PEEK_LOCK
            )
            
            messages_processed = 0
            
            with receiver:
                while True:
                    # Receive batch of messages
                    messages = receiver.receive_messages(
                        max_message_count=100,
                        max_wait_time=5
                    )
                    
                    if not messages:
                        break
                    
                    for message in messages:
                        try:
                            # Extract message content
                            body = str(message)
                            
                            # Convert to JSON if possible
                            try:
                                body_json = json.loads(body)
                            except json.JSONDecodeError:
                                body_json = {"content": body}
                            
                            # Add migration metadata
                            migrated_message = {
                                "original_message": body_json,
                                "migration_metadata": {
                                    "source": "azure-servicebus",
                                    "source_topic": servicebus_topic,
                                    "migrated_at": datetime.utcnow().isoformat() + "Z",
                                    "message_id": str(message.message_id),
                                    "sequence_number": message.sequence_number
                                }
                            }
                            
                            # Publish to Kafka
                            future = self._kafka_producer.send(
                                kafka_topic,
                                value=migrated_message,
                                key=str(message.message_id)
                            )
                            
                            # Wait for confirmation
                            record_metadata = future.get(timeout=30)
                            
                            result.messages_imported += 1
                            
                            # Complete the Service Bus message (remove from queue)
                            if not verify:
                                receiver.complete_message(message)
                            
                            messages_processed += 1
                            
                            if messages_processed % 100 == 0:
                                self.logger.info(f"Processed {messages_processed} messages")
                            
                            if limit > 0 and messages_processed >= limit:
                                break
                        
                        except Exception as e:
                            result.messages_failed += 1
                            result.errors.append(f"Failed to migrate message: {e}")
                            self.logger.warning(f"Failed to migrate message: {e}")
                    
                    if limit > 0 and messages_processed >= limit:
                        break
            
            result.messages_exported = messages_processed
            result.success = result.messages_failed == 0
            
        except Exception as e:
            result.errors.append(f"Migration failed: {e}")
            self.logger.error(f"Migration failed: {e}")
        
        result.duration_seconds = (datetime.now() - start_time).total_seconds()
        return result
    
    def migrate_all_topics(self, limit_per_topic: int = 0, verify: bool = False) -> Dict[str, MigrationResult]:
        """Migrate all configured topics"""
        results = {}
        
        for sb_topic in self.config.topic_mapping.keys():
            self.logger.info(f"Starting migration for topic: {sb_topic}")
            results[sb_topic] = self.migrate_topic(
                sb_topic,
                limit=limit_per_topic,
                verify=verify
            )
        
        return results


def load_config_from_env() -> MigrationConfig:
    """Load configuration from environment variables"""
    return MigrationConfig(
        servicebus_connection_string=os.environ.get("SERVICEBUS_CONNECTION_STRING", ""),
        kafka_bootstrap_servers=os.environ.get("KAFKA_BOOTSTRAP_SERVERS", ""),
        kafka_security_protocol=os.environ.get("KAFKA_SECURITY_PROTOCOL", "PLAINTEXT"),
        kafka_sasl_mechanism=os.environ.get("KAFKA_SASL_MECHANISM", ""),
        kafka_sasl_username=os.environ.get("KAFKA_SASL_USERNAME", ""),
        kafka_sasl_password=os.environ.get("KAFKA_SASL_PASSWORD", "")
    )


def main():
    """Main entry point"""
    parser = argparse.ArgumentParser(
        description="Migrate messages from Azure Service Bus to Apache Kafka"
    )
    
    # Topic selection
    parser.add_argument("--topic", help="Service Bus topic to migrate")
    parser.add_argument("--all-topics", action="store_true", help="Migrate all configured topics")
    parser.add_argument("--subscription", default="migration-sub", help="Subscription name to read from")
    
    # Migration options
    parser.add_argument("--limit", type=int, default=0, help="Maximum messages to migrate (0 = all)")
    parser.add_argument("--verify", action="store_true", help="Verify mode - don't delete from Service Bus")
    parser.add_argument("--dry-run", action="store_true", help="Show what would be migrated")
    
    # Connection options
    parser.add_argument("--sb-connection", help="Service Bus connection string")
    parser.add_argument("--kafka-servers", help="Kafka bootstrap servers")
    
    # Output
    parser.add_argument("--json", action="store_true", help="Output results as JSON")
    parser.add_argument("-l", "--log-level", default="INFO",
                       choices=["DEBUG", "INFO", "WARNING", "ERROR"])
    
    args = parser.parse_args()
    
    # Validate arguments
    if not args.topic and not args.all_topics:
        print("Error: Must specify --topic or --all-topics")
        sys.exit(1)
    
    # Load configuration
    config = load_config_from_env()
    
    # Override from command line
    if args.sb_connection:
        config.servicebus_connection_string = args.sb_connection
    if args.kafka_servers:
        config.kafka_bootstrap_servers = args.kafka_servers
    
    # Validate configuration
    if not config.servicebus_connection_string:
        print("Error: Service Bus connection string required")
        sys.exit(1)
    if not config.kafka_bootstrap_servers:
        print("Error: Kafka bootstrap servers required")
        sys.exit(1)
    
    # Initialize migrator
    migrator = ServiceBusToKafkaMigrator(config, log_level=args.log_level)
    
    if args.dry_run:
        print("Dry run mode - would migrate:")
        if args.all_topics:
            for sb, kafka in config.topic_mapping.items():
                print(f"  {sb} -> {kafka}")
        else:
            kafka_topic = config.topic_mapping.get(args.topic, args.topic)
            print(f"  {args.topic} -> {kafka_topic}")
        sys.exit(0)
    
    try:
        # Connect to services
        if not migrator.connect_servicebus():
            sys.exit(1)
        
        if not migrator.connect_kafka():
            sys.exit(1)
        
        # Run migration
        if args.all_topics:
            results = migrator.migrate_all_topics(
                limit_per_topic=args.limit,
                verify=args.verify
            )
            
            if args.json:
                output = {topic: {
                    "success": r.success,
                    "exported": r.messages_exported,
                    "imported": r.messages_imported,
                    "failed": r.messages_failed,
                    "duration_seconds": r.duration_seconds,
                    "errors": r.errors
                } for topic, r in results.items()}
                print(json.dumps(output, indent=2))
            else:
                print("\nMigration Summary:")
                for topic, r in results.items():
                    status = "✓" if r.success else "✗"
                    print(f"  {status} {topic}: {r.messages_imported}/{r.messages_exported} messages "
                          f"in {r.duration_seconds:.1f}s")
        else:
            result = migrator.migrate_topic(
                args.topic,
                subscription=args.subscription,
                limit=args.limit,
                verify=args.verify
            )
            
            if args.json:
                output = {
                    "success": result.success,
                    "exported": result.messages_exported,
                    "imported": result.messages_imported,
                    "failed": result.messages_failed,
                    "duration_seconds": result.duration_seconds,
                    "errors": result.errors
                }
                print(json.dumps(output, indent=2))
            else:
                status = "Success" if result.success else "Failed"
                print(f"\nMigration {status}")
                print(f"  Exported: {result.messages_exported}")
                print(f"  Imported: {result.messages_imported}")
                print(f"  Failed: {result.messages_failed}")
                print(f"  Duration: {result.duration_seconds:.1f}s")
            
            if not result.success:
                sys.exit(1)
    
    finally:
        migrator.disconnect()
    
    sys.exit(0)


if __name__ == "__main__":
    main()
