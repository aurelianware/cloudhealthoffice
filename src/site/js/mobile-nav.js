// Mobile Navigation Handler
// Shared across all site pages

function initMobileMenu() {
  const menuToggle = document.getElementById('mobileMenuToggle');
  const mainNav = document.getElementById('mainNav');
  
  if (!menuToggle || !mainNav) return;
  
  // Toggle menu on button click
  menuToggle.addEventListener('click', (e) => {
    e.stopPropagation();
    const isOpen = mainNav.classList.toggle('mobile-menu-open');
    menuToggle.setAttribute('aria-expanded', isOpen);
    menuToggle.textContent = isOpen ? '✕' : '☰';
  });
  
  // Close menu when clicking a link
  mainNav.querySelectorAll('a').forEach(link => {
    link.addEventListener('click', () => {
      mainNav.classList.remove('mobile-menu-open');
      menuToggle.setAttribute('aria-expanded', 'false');
      menuToggle.textContent = '☰';
    });
  });
  
  // Close menu when clicking outside
  document.addEventListener('click', (e) => {
    if (!e.target.closest('nav') && mainNav.classList.contains('mobile-menu-open')) {
      mainNav.classList.remove('mobile-menu-open');
      menuToggle.setAttribute('aria-expanded', 'false');
      menuToggle.textContent = '☰';
    }
  });
  
  // Close menu on escape key
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && mainNav.classList.contains('mobile-menu-open')) {
      mainNav.classList.remove('mobile-menu-open');
      menuToggle.setAttribute('aria-expanded', 'false');
      menuToggle.textContent = '☰';
      menuToggle.focus();
    }
  });
}

// Auto-initialize on DOM ready
document.addEventListener('DOMContentLoaded', initMobileMenu);
