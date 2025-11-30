"""
ClaimRiskScorer Azure Function

Triggers on every inbound 837 claim via Service Bus and scores fraud/abuse risk
using a PyTorch model. Generates ZZZ custom segment for 277 response with
risk score and top 3 reasons.

HIPAA Compliance:
- No PHI is logged to Application Insights
- All claim identifiers are anonymized in telemetry
- Risk scores and reasons contain no patient-identifiable information
"""

import json
import logging
import os
import threading
from typing import Any, Optional

import azure.functions as func
from applicationinsights import TelemetryClient

from claim_risk_scorer.model import ClaimRiskModel
from claim_risk_scorer.claim_parser import parse_837_claim, Claim837
from claim_risk_scorer.zzz_segment import generate_zzz_segment

# Configure logging
logger = logging.getLogger(__name__)

# Thread lock for singleton initialization
_init_lock = threading.Lock()

# Initialize Application Insights telemetry client
_telemetry_client: Optional[TelemetryClient] = None


def get_telemetry_client() -> Optional[TelemetryClient]:
    """Get or create Application Insights telemetry client (thread-safe)."""
    global _telemetry_client
    
    if _telemetry_client is None:
        with _init_lock:
            # Double-check locking pattern
            if _telemetry_client is None:
                instrumentation_key = os.environ.get("APPINSIGHTS_INSTRUMENTATIONKEY")
                connection_string = os.environ.get("APPLICATIONINSIGHTS_CONNECTION_STRING")
                
                if connection_string:
                    # Use connection string (preferred)
                    _telemetry_client = TelemetryClient(connection_string=connection_string)
                elif instrumentation_key:
                    # Fall back to instrumentation key
                    _telemetry_client = TelemetryClient(instrumentation_key)
                else:
                    logger.warning("Application Insights not configured - telemetry will be disabled")
                    return None
    
    return _telemetry_client


# Initialize the risk scoring model (singleton)
_risk_model: Optional[ClaimRiskModel] = None


def get_risk_model() -> ClaimRiskModel:
    """Get or create the risk scoring model instance (thread-safe)."""
    global _risk_model
    
    if _risk_model is None:
        with _init_lock:
            # Double-check locking pattern
            if _risk_model is None:
                # Use environment variable for model path, with sensible default
                model_path = os.environ.get("MODEL_PATH", "./ml/claim-fraud-v1.pt")
                _risk_model = ClaimRiskModel(model_path)
    
    return _risk_model


# Risk score thresholds
HIGH_RISK_THRESHOLD = 80


def main(msg: func.ServiceBusMessage) -> None:
    """
    Main Azure Function entry point.
    
    Processes inbound 837 claims from Service Bus, scores fraud/abuse risk,
    and logs high-risk claims to Application Insights.
    
    Args:
        msg: Service Bus message containing 837 claim data
    """
    try:
        # Parse message body
        message_body = msg.get_body().decode("utf-8")
        logger.info("Processing 837 claim message")
        
        # Parse the message - could be JSON or raw EDI
        claim = _parse_claim_message(message_body)
        
        if claim is None:
            logger.warning("Could not parse claim from message")
            raise ValueError("Failed to parse claim from message - invalid format")
        
        # Get risk model and score the claim
        model = get_risk_model()
        risk_result = model.score_claim(claim)
        
        logger.info(
            f"Claim scored: risk_score={risk_result.risk_score}, "
            f"reasons={[r.code for r in risk_result.top_reasons]}"
        )
        
        # Generate ZZZ segment for 277 response
        zzz_segment = generate_zzz_segment(
            risk_score=risk_result.risk_score,
            reasons=risk_result.top_reasons[:3]  # Top 3 reasons
        )
        
        logger.info(f"Generated ZZZ segment: {zzz_segment}")
        
        # Log high-risk claims to Application Insights
        if risk_result.risk_score >= HIGH_RISK_THRESHOLD:
            _log_high_risk_claim(claim, risk_result)
        
        # Success - message will be completed automatically
        logger.info("Claim processing completed successfully")
        
    except json.JSONDecodeError as e:
        logger.error(f"Failed to parse message as JSON: {e}")
        raise
    except ValueError as e:
        logger.error(f"Invalid claim data: {e}")
        raise
    except Exception as e:
        logger.error(f"Unexpected error processing claim: {e}")
        raise


def _parse_claim_message(message_body: str) -> Optional[Claim837]:
    """
    Parse claim from message body.
    
    Supports both JSON and raw EDI formats.
    """
    # Try JSON first
    try:
        data = json.loads(message_body)
        
        # If it's a wrapped message with claim data
        if isinstance(data, dict):
            # Check for EDI content in various fields
            edi_content = data.get("ediContent") or data.get("content") or data.get("payload")
            
            if edi_content:
                return parse_837_claim(edi_content)
            
            # Check if it's already parsed claim data
            if "claimNumber" in data or "claim_number" in data:
                return Claim837.from_dict(data)
        
        logger.warning("Unknown JSON message format")
        return None
        
    except json.JSONDecodeError:
        # Not JSON - try parsing as raw EDI
        if "ISA*" in message_body or "ST*837" in message_body:
            return parse_837_claim(message_body)
        
        logger.warning("Message is neither valid JSON nor EDI format")
        return None


def _log_high_risk_claim(claim: 'Claim837', risk_result: Any) -> None:
    """
    Log high-risk claim event to Application Insights.
    
    Note: We only log anonymized/aggregate data to comply with HIPAA.
    No PHI (member IDs, patient names, etc.) is sent to telemetry.
    """
    telemetry = get_telemetry_client()
    
    if telemetry is None:
        logger.warning("Telemetry client not available - skipping HighRiskClaim event")
        return
    
    # Build properties dict - NO PHI
    properties = {
        "risk_score": str(risk_result.risk_score),
        "risk_category": _get_risk_category(risk_result.risk_score),
        "reason_codes": ",".join([r.code for r in risk_result.top_reasons[:3]]),
        "provider_state": claim.provider_state or "UNKNOWN",
        "claim_type": claim.claim_type or "837P",
        "service_type_code": claim.service_type_code or "UNKNOWN",
        "bill_amount_bucket": _get_amount_bucket(claim.bill_amount),
    }
    
    # Build metrics dict
    metrics = {
        "risk_score": risk_result.risk_score,
        "reason_count": len(risk_result.top_reasons),
    }
    
    # Track custom event
    telemetry.track_event("HighRiskClaim", properties=properties, measurements=metrics)
    telemetry.flush()
    
    logger.info(f"Logged HighRiskClaim event: score={risk_result.risk_score}")


def _get_risk_category(score: float) -> str:
    """Convert risk score to category label."""
    if score >= 81:
        return "CRITICAL"
    elif score >= 61:
        return "HIGH"
    elif score >= 31:
        return "MEDIUM"
    else:
        return "LOW"


def _get_amount_bucket(amount: Optional[float]) -> str:
    """
    Bucket bill amount into ranges for anonymized telemetry.
    
    This prevents identifying specific claims while still allowing
    aggregate analysis of fraud patterns by claim amount.
    """
    if amount is None:
        return "UNKNOWN"
    elif amount < 100:
        return "0-100"
    elif amount < 500:
        return "100-500"
    elif amount < 1000:
        return "500-1000"
    elif amount < 5000:
        return "1000-5000"
    elif amount < 10000:
        return "5000-10000"
    elif amount < 50000:
        return "10000-50000"
    else:
        return "50000+"
