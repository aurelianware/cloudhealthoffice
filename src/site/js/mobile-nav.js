// Mobile Navigation Handler
// Shared across all site pages

function initMobileMenu() {
  const menuToggle = document.getElementById('mobileMenuToggle');
  const mainNav = document.getElementById('mainNav');
  
  if (!menuToggle || !mainNav) return;

  // Helper: collapse the mobile menu
  function closeMenu() {
    mainNav.classList.remove('mobile-menu-open');
    menuToggle.setAttribute('aria-expanded', 'false');
    menuToggle.textContent = '☰';
  }
  
  // Toggle menu on button click
  menuToggle.addEventListener('click', (e) => {
    e.stopPropagation();
    const isOpen = mainNav.classList.toggle('mobile-menu-open');
    menuToggle.setAttribute('aria-expanded', isOpen);
    menuToggle.textContent = isOpen ? '✕' : '☰';
  });
  
  // Close menu when clicking ANY link inside mainNav, including auth links
  // added asynchronously by updateNavigation() — event delegation handles both
  // links present at DOMContentLoaded and those appended later.
  mainNav.addEventListener('click', (e) => {
    if (e.target.closest('a')) {
      closeMenu();
    }
  });
  
  // Close menu when clicking outside
  document.addEventListener('click', (e) => {
    if (!e.target.closest('nav') && mainNav.classList.contains('mobile-menu-open')) {
      closeMenu();
    }
  });
  
  // Close menu on escape key
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && mainNav.classList.contains('mobile-menu-open')) {
      closeMenu();
      menuToggle.focus();
    }
  });
}

// Auto-initialize on DOM ready
document.addEventListener('DOMContentLoaded', initMobileMenu);
