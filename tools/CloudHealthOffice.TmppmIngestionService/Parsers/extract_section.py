#!/usr/bin/env python3
"""Called by TmppmIngestionService to extract sections from TMPPM PDFs using PyMuPDF."""
import fitz, re, json, sys

def extract(pdf_path, section_ref):
    doc = fitz.open(pdf_path)
    full_text = "\n".join(page.get_text() for page in doc)
    
    escaped = re.escape(section_ref)
    matches = list(re.finditer(rf'{escaped}\n', full_text))
    
    for m in matches:
        start = m.start()
        chunk = full_text[start:start+200]
        if '. . . . .' in chunk:
            continue
        
        parts = section_ref.split('.')
        depth = len(parts)
        next_patterns = []
        for i in range(depth, 0, -1):
            prefix = r'\.'.join(re.escape(p) for p in parts[:i-1])
            next_num = int(parts[i-1]) + 1
            if prefix:
                next_patterns.append(rf'\n{prefix}\.{next_num}\n')
            else:
                next_patterns.append(rf'\n{next_num}\n')
        
        combined = '|'.join(next_patterns)
        next_match = re.search(combined, full_text[start+10:])
        end = start + 10 + next_match.start() if next_match else min(start + 10000, len(full_text))
        
        section_text = full_text[start:end]
        cpt = sorted(set(re.findall(r'\b(\d{5})\b', section_text)))
        cpt = [c for c in cpt if int(c) >= 10000]
        hcpcs = sorted(set(re.findall(r'\b([A-V]\d{4})\b', section_text)))
        dx = sorted(set(re.findall(r'\b([A-Z]\d{2}\.?\d{0,4})\b', section_text)))
        
        result = {
            "sectionRef": section_ref,
            "found": True,
            "textLength": len(section_text),
            "text": section_text,
            "cptCodes": cpt,
            "hcpcsCodes": hcpcs,
            "dxCodes": dx,
            "paRequired": bool(re.search(r'prior\s+auth', section_text, re.IGNORECASE))
        }
        json.dump(result, sys.stdout)
        return
    
    json.dump({"sectionRef": section_ref, "found": False}, sys.stdout)

if __name__ == "__main__":
    extract(sys.argv[1], sys.argv[2])
