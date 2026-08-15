-- Reference Data Database Initialization Script
-- Cloud Health Office - Reference Data Service
-- PostgreSQL 16

-- CPT Codes Table (~44,000 codes)
CREATE TABLE IF NOT EXISTS cpt_codes (
    code VARCHAR(5) PRIMARY KEY,
    short_description VARCHAR(100) NOT NULL,
    long_description TEXT,
    category VARCHAR(20) NOT NULL,
    section VARCHAR(100),
    subsection VARCHAR(200),
    modifier_exempt BOOLEAN DEFAULT FALSE,
    status_code VARCHAR(1) NOT NULL,
    effective_date DATE,
    end_date DATE,
    requires_prior_auth BOOLEAN DEFAULT FALSE,
    medicare_payment DECIMAL(10,2)
);

CREATE INDEX idx_cpt_status ON cpt_codes(status_code);
CREATE INDEX idx_cpt_section ON cpt_codes(section);
CREATE INDEX idx_cpt_short_desc ON cpt_codes USING GIN (to_tsvector('english', short_description));
CREATE INDEX idx_cpt_long_desc ON cpt_codes USING GIN (to_tsvector('english', long_description));

-- ICD-10-CM Codes Table (~70,000 codes)
CREATE TABLE IF NOT EXISTS icd10_codes (
    code VARCHAR(10) PRIMARY KEY,
    short_description VARCHAR(100) NOT NULL,
    long_description TEXT,
    category_chapter VARCHAR(100),
    billable BOOLEAN DEFAULT TRUE,
    seventh_char_required BOOLEAN DEFAULT FALSE,
    valid_seventh_chars VARCHAR(20),
    laterality_required BOOLEAN DEFAULT FALSE,
    status_code VARCHAR(1) NOT NULL,
    effective_date DATE,
    end_date DATE
);

CREATE INDEX idx_icd10_status ON icd10_codes(status_code);
CREATE INDEX idx_icd10_billable ON icd10_codes(billable);
CREATE INDEX idx_icd10_category ON icd10_codes(category_chapter);
CREATE INDEX idx_icd10_short_desc ON icd10_codes USING GIN (to_tsvector('english', short_description));
CREATE INDEX idx_icd10_long_desc ON icd10_codes USING GIN (to_tsvector('english', long_description));

-- HCPCS Level II Codes Table (~8,000 codes)
CREATE TABLE IF NOT EXISTS hcpcs_codes (
    code VARCHAR(5) PRIMARY KEY,
    short_description VARCHAR(100) NOT NULL,
    long_description TEXT,
    category VARCHAR(50),
    status_code VARCHAR(1) NOT NULL,
    coverage_level VARCHAR(1),
    effective_date DATE,
    end_date DATE,
    medicare_payment DECIMAL(10,2)
);

CREATE INDEX idx_hcpcs_status ON hcpcs_codes(status_code);
CREATE INDEX idx_hcpcs_category ON hcpcs_codes(category);
CREATE INDEX idx_hcpcs_short_desc ON hcpcs_codes USING GIN (to_tsvector('english', short_description));

-- Modifiers Table (~100 codes)
CREATE TABLE IF NOT EXISTS modifiers (
    code VARCHAR(2) PRIMARY KEY,
    description VARCHAR(200) NOT NULL,
    category VARCHAR(50),
    price_impact DECIMAL(3,2) DEFAULT 1.00,
    status VARCHAR(1) NOT NULL
);

CREATE INDEX idx_modifier_status ON modifiers(status);

-- DRG Codes Table (~750 MS-DRG codes)
CREATE TABLE IF NOT EXISTS drg_codes (
    code VARCHAR(3) PRIMARY KEY,
    description VARCHAR(200) NOT NULL,
    mdc VARCHAR(2),
    mdc_description VARCHAR(100),
    drg_type VARCHAR(10),
    relative_weight DECIMAL(6,4) NOT NULL,
    geometric_mean_los DECIMAL(4,1),
    arithmetic_mean_los DECIMAL(4,1),
    fiscal_year INTEGER NOT NULL,
    status VARCHAR(1) NOT NULL
);

CREATE INDEX idx_drg_mdc ON drg_codes(mdc);
CREATE INDEX idx_drg_fiscal_year ON drg_codes(fiscal_year);
CREATE INDEX idx_drg_status ON drg_codes(status);

-- Place of Service Codes Table (~60 codes)
CREATE TABLE IF NOT EXISTS place_of_service (
    code VARCHAR(2) PRIMARY KEY,
    description VARCHAR(200) NOT NULL,
    category VARCHAR(50),
    status VARCHAR(1) NOT NULL
);

CREATE INDEX idx_pos_status ON place_of_service(status);

-- Revenue Codes Table (~1,000 codes)
CREATE TABLE IF NOT EXISTS revenue_codes (
    code VARCHAR(4) PRIMARY KEY,
    description VARCHAR(200) NOT NULL,
    category VARCHAR(50),
    status VARCHAR(1) NOT NULL
);

CREATE INDEX idx_revenue_status ON revenue_codes(status);
CREATE INDEX idx_revenue_category ON revenue_codes(category);

-- Insert sample data for testing

-- Sample CPT codes
INSERT INTO cpt_codes (code, short_description, long_description, category, section, status_code, requires_prior_auth, medicare_payment)
VALUES 
    ('99213', 'Office Visit Level 3', 'Office or other outpatient visit, established patient, 20-29 minutes', 'Category I', 'Evaluation and Management', 'A', false, 93.20),
    ('99214', 'Office Visit Level 4', 'Office or other outpatient visit, established patient, 30-39 minutes', 'Category I', 'Evaluation and Management', 'A', false, 131.20),
    ('27447', 'Total Knee Replacement', 'Arthroplasty, knee, condyle and plateau; medial OR lateral compartment', 'Category I', 'Surgery', 'A', true, 1245.00),
    ('70450', 'CT Head/Brain w/o contrast', 'Computed tomography, head or brain; without contrast material', 'Category I', 'Radiology', 'A', true, 187.50)
ON CONFLICT (code) DO NOTHING;

-- Sample ICD-10 codes
INSERT INTO icd10_codes (code, short_description, long_description, category_chapter, billable, status_code)
VALUES 
    ('E11.9', 'Type 2 diabetes w/o complications', 'Type 2 diabetes mellitus without complications', 'Endocrine, Nutritional and Metabolic', true, 'A'),
    ('I10', 'Essential hypertension', 'Essential (primary) hypertension', 'Circulatory System', true, 'A'),
    ('M17.11', 'Unilateral primary OA, right knee', 'Unilateral primary osteoarthritis, right knee', 'Musculoskeletal', true, 'A'),
    ('Z00.00', 'Encounter for general adult medical exam w/o abnormal findings', 'Encounter for general adult medical examination without abnormal findings', 'Factors Influencing Health Status', true, 'A')
ON CONFLICT (code) DO NOTHING;

-- Sample HCPCS codes
INSERT INTO hcpcs_codes (code, short_description, long_description, category, status_code, coverage_level)
VALUES 
    ('J1817', 'Insulin injection', 'Injection, insulin for administration through DME (i.e., insulin pump) per 50 units', 'Drugs', 'A', 'C'),
    ('E0601', 'CPAP device', 'Continuous positive airway pressure (CPAP) device', 'Durable Medical Equipment', 'A', 'C')
ON CONFLICT (code) DO NOTHING;

-- Sample Modifiers
INSERT INTO modifiers (code, description, category, price_impact, status)
VALUES 
    ('50', 'Bilateral Procedure', 'Surgery', 1.50, 'A'),
    ('26', 'Professional Component', 'Professional/Technical', 0.50, 'A'),
    ('TC', 'Technical Component', 'Professional/Technical', 0.50, 'A'),
    ('59', 'Distinct Procedural Service', 'NCCI Edit', 1.00, 'A')
ON CONFLICT (code) DO NOTHING;

-- Sample Place of Service codes
INSERT INTO place_of_service (code, description, category, status)
VALUES 
    ('11', 'Office', 'Non-Facility', 'A'),
    ('21', 'Inpatient Hospital', 'Facility', 'A'),
    ('22', 'Outpatient Hospital', 'Facility', 'A'),
    ('23', 'Emergency Room - Hospital', 'Facility', 'A')
ON CONFLICT (code) DO NOTHING;

-- Sample Revenue codes
INSERT INTO revenue_codes (code, description, category, status)
VALUES 
    ('0450', 'Emergency Room', 'Emergency Services', 'A'),
    ('0250', 'Pharmacy', 'Ancillary', 'A'),
    ('0360', 'Operating Room', 'Room & Board', 'A')
ON CONFLICT (code) DO NOTHING;

-- Sample DRG codes (FY 2024)
INSERT INTO drg_codes (code, description, mdc, mdc_description, drg_type, relative_weight, geometric_mean_los, arithmetic_mean_los, fiscal_year, status)
VALUES 
    ('470', 'Major Hip and Knee Joint Replacement or Reattachment of Lower Extremity w/o MCC', '08', 'Musculoskeletal', 'SURG', 1.8567, 2.3, 2.8, 2024, 'A'),
    ('291', 'Heart Failure & Shock w MCC', '05', 'Circulatory', 'MED', 1.3254, 4.1, 5.2, 2024, 'A')
ON CONFLICT (code) DO NOTHING;
