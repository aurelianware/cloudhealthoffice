// Basic X12 270 EDI structure for mapping; extend as needed
export interface X12_270 {
  inquiryId: string;
  member: {
    id: string;
    firstName: string;
    lastName: string;
    gender: string;
    dob: string; // YYYY-MM-DD
  };
  insurerId: string;
}