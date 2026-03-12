/**
 * Cloud Health Office - Authentication Helper Library
 * Provides authentication utilities for multi-tenant Microsoft Entra ID integration
 */

/**
 * Load user profile from Azure Static Web Apps authentication endpoint
 * @returns {Promise<Object|null>} User profile object or null if not authenticated
 */
async function loadUserProfile() {
  try {
    const response = await fetch('/.auth/me');
    if (!response.ok) {
      return null;
    }
    const payload = await response.json();
    const { clientPrincipal } = payload;
    return clientPrincipal;
  } catch (error) {
    console.error('Error loading user profile:', error);
    return null;
  }
}

/**
 * Check if user is authenticated
 * @returns {Promise<boolean>} True if user is authenticated, false otherwise
 */
async function isAuthenticated() {
  const user = await loadUserProfile();
  return user !== null;
}

/**
 * Redirect to login if user is not authenticated
 * @param {string} returnUrl - URL to redirect to after successful login (default: current page)
 */
async function requireAuth(returnUrl = null) {
  const authenticated = await isAuthenticated();
  if (!authenticated) {
    const redirectUrl = returnUrl || window.location.pathname + window.location.search;
    window.location.href = `/.auth/login/aad?post_login_redirect_uri=${encodeURIComponent(redirectUrl)}`;
  }
}

/**
 * Make an authenticated API call
 * @param {string} url - API endpoint URL
 * @param {Object} options - Fetch options (method, headers, body, etc.)
 * @returns {Promise<Response>} Fetch response
 */
async function callAuthenticatedAPI(url, options = {}) {
  const authenticated = await isAuthenticated();
  if (!authenticated) {
    throw new Error('User is not authenticated. Please log in first.');
  }

  const defaultOptions = {
    headers: {
      'Content-Type': 'application/json',
    },
  };

  const mergedOptions = {
    ...defaultOptions,
    ...options,
    headers: {
      ...defaultOptions.headers,
      ...(options.headers || {}),
    },
  };

  try {
    const response = await fetch(url, mergedOptions);
    
    // Handle 401 Unauthorized by redirecting to login
    if (response.status === 401) {
      window.location.href = `/.auth/login/aad?post_login_redirect_uri=${encodeURIComponent(window.location.pathname)}`;
      return response;
    }
    
    return response;
  } catch (error) {
    console.error('API call failed:', error);
    throw error;
  }
}

/**
 * Log out the current user
 * @param {string} returnUrl - URL to redirect to after logout (default: home page)
 */
function logout(returnUrl = '/') {
  window.location.href = `/.auth/logout?post_logout_redirect_uri=${encodeURIComponent(returnUrl)}`;
}

/**
 * Get user display name
 * @returns {Promise<string>} User display name or 'Guest' if not authenticated
 */
async function getUserDisplayName() {
  const user = await loadUserProfile();
  if (!user) {
    return 'Guest';
  }
  return user.userDetails || user.userId || 'User';
}

/**
 * Update navigation with user info and auth links
 * @param {string} navSelector - CSS selector for navigation element
 */
async function updateNavigation(navSelector = 'nav ul') {
  const authenticated = await isAuthenticated();
  const nav = document.querySelector(navSelector);
  
  if (!nav) {
    //console.warn('Navigation element not found:', navSelector);
    return;
  }

  // Remove existing dynamic auth links if present
  const existingAuthLinks = nav.querySelectorAll('.auth-link');
  existingAuthLinks.forEach(link => link.remove());

  if (authenticated) {
    // Find and remove the static Sign In link (added in HTML) so we can replace it with the user menu
    const staticSignIn = nav.querySelector('a[href="/login"], a[href="/login.html"], a[href="../login.html"]');
    if (staticSignIn) {
      staticSignIn.closest('li')?.remove();
    }
    const userName = await getUserDisplayName();

    // Add user menu
    const userMenuItem = document.createElement('li');
    userMenuItem.className = 'auth-link';
    const portalLink = document.createElement('a');
    portalLink.href = '/portal/';
    portalLink.style.color = '#00ff88';
    portalLink.textContent = `Portal (${userName})`;
    userMenuItem.appendChild(portalLink);
    nav.appendChild(userMenuItem);

    // Add logout link
    const logoutItem = document.createElement('li');
    logoutItem.className = 'auth-link';
    const logoutLink = document.createElement('a');
    logoutLink.href = '#';
    logoutLink.style.color = '#888';
    logoutLink.textContent = 'Sign Out';
    logoutLink.addEventListener('click', (e) => {
      e.preventDefault();
      logout();
    });
    logoutItem.appendChild(logoutLink);
    nav.appendChild(logoutItem);
  }
}

// Export functions for use in modules (if needed)
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    loadUserProfile,
    isAuthenticated,
    requireAuth,
    callAuthenticatedAPI,
    logout,
    getUserDisplayName,
    updateNavigation,
  };
}
