# Machine Learning Models

This directory contains machine learning models used by the Cloud Health Office platform.

## claim-fraud-v1.pt

A PyTorch model for scoring fraud/abuse risk on healthcare claims (837 transactions).

### Model Specification

- **Input**: Claim features tensor with the following attributes:
  - `bill_amount`: Total billed amount
  - `provider_risk_score`: Historical provider risk (0-1)
  - `member_tenure_days`: Member enrollment duration
  - `procedure_code_risk`: Procedure code risk category (0-1)
  - `diagnosis_code_count`: Number of diagnosis codes
  - `modifier_count`: Number of modifiers used
  - `service_days`: Number of service days
  - `out_of_network`: Binary indicator for out-of-network (0 or 1)

- **Output**: Risk score (0-100)
  - 0-30: Low risk
  - 31-60: Medium risk  
  - 61-80: High risk
  - 81-100: Critical risk (triggers "HighRiskClaim" event)

### Risk Reasons

The model provides top 3 reasons for the risk score from:
- HIGH_BILL_AMOUNT: Unusually high billed amount
- PROVIDER_HISTORY: Provider has fraud history flags
- PROCEDURE_MISMATCH: Procedure doesn't match diagnosis
- DUPLICATE_PATTERN: Potential duplicate claim pattern
- UNBUNDLING: Possible code unbundling detected
- UPCODING: Potential upcoding detected
- OUT_OF_NETWORK: Out-of-network provider flagged
- NEW_MEMBER: Recent enrollment with high-cost claims

### Usage

The model is automatically loaded by the `ClaimRiskScorer` Azure Function when processing inbound 837 claims via Service Bus.

### Training

To retrain the model:
1. Collect labeled historical claim data with fraud outcomes
2. Extract features using the specification above
3. Train using PyTorch (see scripts/train-fraud-model.py if available)
4. Save the model state dict to this directory

### HIPAA Compliance

- Model training uses only de-identified aggregate data
- No PHI is stored in the model weights
- Feature extraction redacts all personally identifiable information
